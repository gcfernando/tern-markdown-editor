using System.Security.Cryptography;
using System.Text;
using Tern.Domain.Documents;
using Tern.Domain.Text;

namespace Tern.Filesystem;

public sealed record LoadedFile(
    string Path,
    string Text,
    TextFileFormat Format,
    DiskBaseline Baseline,
    DecodeFailure DecodeFailure)
{
    /// <summary>Documents that cannot be re-encoded identically open read-only so a save can never corrupt them.</summary>
    public bool ShouldOpenReadOnly => DecodeFailure != DecodeFailure.None;
}

public enum ConflictKind
{
    /// <summary>The file on disk no longer matches what was loaded.</summary>
    ModifiedOnDisk,

    /// <summary>The file was deleted or moved since it was loaded.</summary>
    DeletedOnDisk,

    /// <summary>A save to a new path would replace an unrelated existing file.</summary>
    TargetExists,
}

public abstract record SaveOutcome;

public sealed record SaveSucceeded(DiskBaseline Baseline) : SaveOutcome;

public sealed record SaveConflict(ConflictKind Kind, DiskBaseline? Disk) : SaveOutcome;

public sealed record SaveFailed(string Reason, Exception? Exception = null) : SaveOutcome;

/// <summary>Reads and writes documents with optimistic concurrency against a disk baseline.</summary>
public sealed class DocumentFileService
{
    // The current editor and parser require contiguous in-memory documents. Refuse
    // oversized inputs before allocation until an indexed large-file buffer exists.
    public const long MaximumEagerLoadBytes = 64L * 1024 * 1024;

    // Deterministic interleaving for filesystem contract tests; never set by the application.
    internal Action<string>? BeforeReplace { get; set; }

    // Actual persistence boundaries for deterministic fault/crash tests. Null in the application.
    internal Action<string>? SavePhase { get; set; }

    // Deterministic post-read interleaving for document lifecycle tests; null in the application.
    internal Func<string, Task>? AfterReadForLoadAsync { get; set; }

    public async Task<LoadedFile> LoadAsync(string path, TextEncodingKind? forcedEncoding = null, CancellationToken cancellationToken = default)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        var bytes = await ReadAllBytesSharedAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (AfterReadForLoadAsync is { } afterRead) await afterRead(fullPath).ConfigureAwait(false);
        var decoded = TextFileCodec.Decode(bytes, forcedEncoding);
        var baseline = new DiskBaseline(bytes.LongLength, info.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(bytes)));
        return new LoadedFile(fullPath, decoded.Text, decoded.Format, baseline, decoded.Failure);
    }

    /// <summary>Hashes the file as it currently is on disk; null when it does not exist.</summary>
    public async Task<DiskBaseline?> ReadBaselineAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
            var length = stream.Length;
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return new DiskBaseline(length, new FileInfo(path).LastWriteTimeUtc, Convert.ToHexString(hash));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Saves against a content precondition. Checks refuse observed conflicts before replacement.
    /// Filesystem replacement is not compare-and-swap: the displaced file is retained until verified,
    /// and a late competing version is preserved in a named backup and reported as a failure.
    /// </summary>
    public async Task<SaveOutcome> SaveAsync(
        string path,
        string text,
        TextFileFormat format,
        DiskBaseline? expected,
        bool overwriteExisting = false,
        CancellationToken cancellationToken = default)
    {
        var fullPath = System.IO.Path.GetFullPath(path);

        byte[] bytes;
        string? backupPath = null;
        FileStream? displacedVerification = null;
        try
        {
            bytes = TextFileCodec.Encode(text, format);
        }
        catch (EncoderFallbackException ex)
        {
            return new SaveFailed($"The text contains characters that cannot be stored as {format.EncodingDisplayName}.", ex);
        }

        try
        {
            var conflict = await CheckPreconditionAsync(fullPath, expected, overwriteExisting, cancellationToken).ConfigureAwait(false);
            if (conflict is not null)
            {
                return conflict;
            }

            var targetPath = ResolveWriteTarget(fullPath);
            // Also anchor an explicitly approved Save As overwrite to the version observed now.
            var replacementBaseline = expected ?? await ReadBaselineAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (replacementBaseline is not null)
            {
                var currentBytes = await ReadAllBytesSharedAsync(targetPath, cancellationToken).ConfigureAwait(false);
                if (currentBytes.AsSpan().SequenceEqual(bytes))
                {
                    return new SaveSucceeded(new DiskBaseline(currentBytes.LongLength, new FileInfo(targetPath).LastWriteTimeUtc,
                        Convert.ToHexString(SHA256.HashData(currentBytes))));
                }
            }
            var directory = System.IO.Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(directory);

            var tempPath = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tern-tmp");
            try
            {
                await WriteDurablyAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
                CopyMetadata(targetPath, tempPath);
                SavePhase?.Invoke("flushed-before-replace");

                // Re-check immediately before the swap to shrink the window for a concurrent writer.
                var lateConflict = await CheckPreconditionAsync(fullPath, replacementBaseline, overwriteExisting: false, cancellationToken).ConfigureAwait(false);
                if (lateConflict is not null)
                {
                    return lateConflict;
                }

                if (!PathsEqual(ResolveWriteTarget(fullPath), targetPath))
                {
                    return new SaveFailed("The file link changed while saving. Nothing was overwritten; review the current destination before saving again.");
                }

                BeforeReplace?.Invoke(targetPath);
                if (replacementBaseline is not null)
                {
                    backupPath = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tern-save-backup");
                    for (var attempt = 0; ; attempt++)
                    {
                        try { ReplaceFile(tempPath, targetPath, backupPath); break; }
                        catch (IOException ex) when (OperatingSystem.IsWindows() && attempt < 6
                            && (ex.HResult & 0xffff) is 32 or 33 or 1175
                            && File.Exists(tempPath) && !File.Exists(backupPath))
                        {
                            // Windows readers can briefly deny replacement. Retry only an
                            // untouched swap, retaining all concurrent-write/link safeguards.
                            await Task.Delay(25 * (attempt + 1), cancellationToken).ConfigureAwait(false);
                            var retryConflict = await CheckPreconditionAsync(fullPath, replacementBaseline,
                                overwriteExisting: false, cancellationToken).ConfigureAwait(false);
                            if (retryConflict is not null) return retryConflict;
                            if (!PathsEqual(ResolveWriteTarget(fullPath), targetPath))
                                return new SaveFailed("The file link changed while saving. Nothing was overwritten; review the current destination before saving again.");
                        }
                    }
                    SavePhase?.Invoke("replaced-before-verify");
                    // On Windows this sharing mode refuses acquisition while any writable
                    // handle still refers to the displaced inode. Keep it until backup deletion:
                    // a retained writer must never lose its last named copy after our hash.
                    displacedVerification = new FileStream(backupPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read | FileShare.Delete, 81920, useAsync: true);
                    var displacedLength = displacedVerification.Length;
                    var displacedHash = Convert.ToHexString(await SHA256.HashDataAsync(displacedVerification, CancellationToken.None).ConfigureAwait(false));
                    if (displacedLength != replacementBaseline.Length || displacedHash != replacementBaseline.Sha256)
                    {
                        return new SaveFailed($"Another program changed the file during replacement. Its version is preserved at '{backupPath}'. Your buffer remains unsaved. Compare both versions before continuing.");
                    }
                }
                else
                {
                    // Never overwrite a file created after the precondition check.
                    File.Move(tempPath, targetPath);
                    SavePhase?.Invoke("replaced-before-verify");
                }
            }
            finally
            {
                TryDelete(tempPath);
            }

            if (!PathsEqual(ResolveWriteTarget(fullPath), targetPath))
            {
                return new SaveFailed("The file link changed during replacement. Review the destination before continuing." + BackupNotice(backupPath));
            }
            var written = await ReadAllBytesSharedAsync(targetPath, cancellationToken).ConfigureAwait(false);
            if (!written.AsSpan().SequenceEqual(bytes))
            {
                return new SaveFailed("The file on disk does not match what was written. Your changes are still open." + BackupNotice(backupPath));
            }

            var baseline = new DiskBaseline(written.LongLength, new FileInfo(targetPath).LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(written)));
            // File sharing is not a portable writer-exclusion guarantee. Unsupported
            // platforms keep the named backup rather than assuming a reader excludes writers.
            if (backupPath is not null && OperatingSystem.IsWindows()) TryDelete(backupPath);
            return new SaveSucceeded(baseline);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SaveFailed("Tern does not have permission to write this file, or the file is read-only." + BackupNotice(backupPath), ex);
        }
        catch (IOException ex)
        {
            return new SaveFailed($"The file could not be written: {ex.Message}" + BackupNotice(backupPath), ex);
        }
        finally
        {
            if (displacedVerification is not null) await displacedVerification.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<SaveOutcome?> CheckPreconditionAsync(string path, DiskBaseline? expected, bool overwriteExisting, CancellationToken ct)
    {
        var current = await ReadBaselineAsync(path, ct).ConfigureAwait(false);
        if (expected is null)
        {
            return current is not null && !overwriteExisting ? new SaveConflict(ConflictKind.TargetExists, current) : null;
        }

        if (current is null)
        {
            return new SaveConflict(ConflictKind.DeletedOnDisk, null);
        }

        return current.SameContentAs(expected) ? null : new SaveConflict(ConflictKind.ModifiedOnDisk, current);
    }

    // Writing through a symbolic link must update the link target, not replace the link itself.
    private static string ResolveWriteTarget(string path)
    {
        return FileIdentity.ResolvePhysicalPath(path);
    }

    private async Task WriteDurablyAsync(string path, byte[] bytes, CancellationToken ct)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
            BufferSize = 81920,
        });
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        SavePhase?.Invoke("temp-written-before-flush");
        await stream.FlushAsync(ct).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void CopyMetadata(string existingPath, string tempPath)
    {
        if (!File.Exists(existingPath))
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempPath, File.GetUnixFileMode(existingPath));
        }
    }

    private static bool PathsEqual(string first, string second) => string.Equals(first, second,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string BackupNotice(string? path) => path is not null && File.Exists(path)
        ? $" The displaced file is preserved at '{path}'." : string.Empty;

    private static void ReplaceFile(string tempPath, string targetPath, string backupPath)
    {
        if (File.Exists(targetPath))
        {
            if ((File.GetAttributes(targetPath) & FileAttributes.ReadOnly) != 0)
            {
                throw new UnauthorizedAccessException("The file is read-only.");
            }

            File.Replace(tempPath, targetPath, backupPath, ignoreMetadataErrors: false);
        }
        else
        {
            throw new FileNotFoundException("The destination disappeared before replacement.", targetPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<byte[]> ReadAllBytesSharedAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        if (stream.Length > MaximumEagerLoadBytes)
            throw new IOException($"This file is {stream.Length:N0} bytes. Tern's current editor can open files up to 64 MiB (67,108,864 bytes). A virtualized large-file editor is required for larger files; the file has not been changed.");
        var buffer = new byte[stream.Length];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }
}
