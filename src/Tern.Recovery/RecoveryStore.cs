using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tern.Domain.Text;
using Tern.Filesystem;

namespace Tern.Recovery;

/// <summary>Unsaved work captured for one document session.</summary>
public sealed record RecoveryRecord(
    Guid SessionId,
    string? OriginalPath,
    string DisplayName,
    long Revision,
    DateTime SavedAtUtc,
    string? BaselineSha256,
    TextFileFormat Format,
    string Text,
    bool IsExternalDraft = false,
    string? SourceUrl = null);

public sealed record RecoveryEntry(RecoveryRecord Record, bool OwnerIsAlive);

/// <summary>A checkpoint file that could not be trusted; reported rather than silently deleted.</summary>
public sealed record CorruptRecovery(string Path, string Reason);

public sealed record RecoveryScan(IReadOnlyList<RecoveryEntry> Entries, IReadOnlyList<CorruptRecovery> Corrupt);

/// <summary>
/// Stores whole-document checkpoints in application-local storage, completely separate from saving.
/// Each checkpoint is integrity-checked; the two newest generations are kept so an interrupted write
/// never leaves a session without a readable copy.
/// </summary>
public sealed class RecoveryStore
{
    private static readonly byte[] LegacyMagic = "TERNREC1"u8.ToArray();
    private static readonly byte[] Magic = "TERNREC2"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const int GenerationsToKeep = 2;
    private const long MaxPayloadBytes = DocumentFileService.MaximumEagerLoadBytes * 3;
    private const int MaxHeaderBytes = 1024 * 1024;
    private const long MaxCheckpointBytes = MaxPayloadBytes + MaxHeaderBytes + 4096;
    private const string Extension = ".tern-recovery";
    private readonly string _directory;
    private readonly bool _protectCopies;
    private readonly int _ownerPid = Environment.ProcessId;
    private readonly long _ownerStartTicks = SafeProcessStartTicks(Environment.ProcessId);

    // Actual persistence boundaries for deterministic fault/crash tests. Null in the application.
    internal Action<string>? CheckpointPhase { get; set; }

    public RecoveryStore(string directory, bool protectCopies = false)
    {
        _directory = directory;
        _protectCopies = protectCopies;
        Directory.CreateDirectory(_directory);
    }

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tern", "recovery");

    public Task WriteCheckpointAsync(RecoveryRecord record, CancellationToken cancellationToken = default) =>
        WriteCheckpointCoreAsync(record, _ownerPid, _ownerStartTicks, cancellationToken);

    /// <summary>
    /// After a document or folder is renamed or moved, points checkpoints left by sessions that no longer run at the new path
    /// so a later recovery still finds the right file. Checkpoints owned by a running session are left to that session. Returns how many were rewritten.
    /// </summary>
    public async Task<int> RewriteOriginalPathsAsync(Func<string, string?> map)
    {
        var rewritten = 0;
        foreach (var entry in Scan().Entries.Where(e => !e.OwnerIsAlive && e.Record.OriginalPath is not null))
        {
            if (map(entry.Record.OriginalPath!) is not { } moved) continue;
            await WriteCheckpointCoreAsync(entry.Record with { OriginalPath = moved }, 0, 0, default).ConfigureAwait(false);
            rewritten++;
        }
        return rewritten;
    }

    private async Task WriteCheckpointCoreAsync(RecoveryRecord record, int ownerPid, long ownerStartTicks, CancellationToken cancellationToken)
    {
        // The folder may have been removed while the app runs (cleanup tools, sync); recreate it so protection resumes.
        Directory.CreateDirectory(_directory);
        var generation = NextGeneration(record.SessionId);
        var path = FilePath(record.SessionId, generation);
        var tempPath = path + ".tmp";

        if (record.Text.Length > DocumentFileService.MaximumEagerLoadBytes)
            throw new IOException("The recovery checkpoint exceeds the 64 Mi-character limit.");
        byte[] payload;
        var magic = Magic;
        try { payload = StrictUtf8.GetBytes(record.Text); }
        catch (EncoderFallbackException)
        {
            // Preserve unpaired UTF-16 surrogates exactly, as older checkpoints did.
            payload = MemoryMarshal.AsBytes(record.Text.AsSpan()).ToArray();
            magic = LegacyMagic;
        }
        var header = new Header
        {
            SessionId = record.SessionId,
            OriginalPath = record.OriginalPath,
            IsExternalDraft = record.IsExternalDraft,
            SourceUrl = record.SourceUrl,
            DisplayName = record.DisplayName,
            Revision = record.Revision,
            SavedAtUtc = record.SavedAtUtc,
            BaselineSha256 = record.BaselineSha256,
            Encoding = record.Format.Encoding,
            HasBom = record.Format.HasBom,
            DominantLineEnding = record.Format.DominantLineEnding,
            HasMixedLineEndings = record.Format.HasMixedLineEndings,
            EndsWithNewline = record.Format.EndsWithNewline,
            OwnerPid = ownerPid,
            OwnerStartTicks = ownerStartTicks,
            PayloadLength = payload.Length,
            PayloadSha256 = Convert.ToHexString(SHA256.HashData(payload)),
        };
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header);
        if (headerBytes.Length > MaxHeaderBytes || payload.Length > MaxPayloadBytes)
        {
            throw new IOException("The recovery checkpoint exceeds its metadata or payload size limit.");
        }

        var checkpoint = new byte[magic.Length + 4 + headerBytes.Length + payload.Length];
        magic.CopyTo(checkpoint, 0);
        BitConverter.GetBytes(headerBytes.Length).CopyTo(checkpoint, magic.Length);
        headerBytes.CopyTo(checkpoint, magic.Length + 4);
        payload.CopyTo(checkpoint, magic.Length + 4 + headerBytes.Length);
        if (_protectCopies) checkpoint = UserDataProtection.Protect(checkpoint);
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        {
            await stream.WriteAsync(checkpoint, cancellationToken).ConfigureAwait(false);
            CheckpointPhase?.Invoke("payload-written-before-flush");
            stream.Flush(flushToDisk: true);
        }

        CheckpointPhase?.Invoke("flushed-before-publish");
        File.Move(tempPath, path, overwrite: true);
        CheckpointPhase?.Invoke("published-before-prune");
        PruneOldGenerations(record.SessionId);
    }

    public RecoveryScan Scan()
    {
        var entries = new List<RecoveryEntry>();
        var corrupt = new List<CorruptRecovery>();

        var bySession = Directory.EnumerateFiles(_directory, "*" + Extension)
            .Select(p => (Path: p, Parsed: ParseName(p)))
            .Where(x => x.Parsed is not null)
            .GroupBy(x => x.Parsed!.Value.SessionId);

        foreach (var group in bySession)
        {
            RecoveryEntry? best = null;
            foreach (var file in group.OrderByDescending(x => x.Parsed!.Value.Generation))
            {
                var result = TryRead(file.Path, out var entry, out var reason);
                if (result)
                {
                    best = entry;
                    break;
                }

                corrupt.Add(new CorruptRecovery(file.Path, reason));
            }

            if (best is not null)
            {
                entries.Add(best);
            }
        }

        return new RecoveryScan([.. entries.OrderByDescending(e => e.Record.SavedAtUtc)], corrupt);
    }

    /// <summary>
    /// Removes all checkpoints for a session once its content is safely on disk or deliberately discarded.
    /// Never throws: a missing or unreadable recovery folder returns false so a completed save is not undone by cleanup.
    /// </summary>
    public bool Retire(Guid sessionId)
    {
        try
        {
            if (!Directory.Exists(_directory)) return true;
            foreach (var path in Directory.EnumerateFiles(_directory, $"{sessionId:N}.*{Extension}"))
            {
                TryDelete(path);
                if (File.Exists(path)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }


    public string DirectoryPath => _directory;

    /// <summary>Where unreadable checkpoints are kept after they have been reported once. <see cref="Scan"/> never looks here.</summary>
    public string DamagedDirectory => Path.Combine(_directory, "damaged");

    /// <summary>Deletes only unreadable checkpoint files that live directly in the recovery folder; returns how many were removed.</summary>
    public int DeleteDamaged(IEnumerable<string> paths)
    {
        var removed = 0;
        foreach (var path in paths.Where(IsCheckpointFile))
        {
            TryDelete(path);
            if (!File.Exists(path)) removed++;
        }
        return removed;
    }

    /// <summary>Moves unreadable checkpoints to <c>damaged\</c> so they are reported once, never again, and never lost. Returns how many moved.</summary>
    public int Quarantine(IEnumerable<string> paths)
    {
        var moved = 0;
        foreach (var path in paths.Where(IsCheckpointFile))
        {
            try
            {
                System.IO.Directory.CreateDirectory(DamagedDirectory);
                var target = Path.Combine(DamagedDirectory, Path.GetFileName(path));
                for (var n = 1; File.Exists(target); n++)
                    target = Path.Combine(DamagedDirectory, $"{Path.GetFileNameWithoutExtension(path)}.{n}{Extension}");
                File.Move(path, target);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return moved;
    }

    private bool IsCheckpointFile(string path) =>
        File.Exists(path) && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(_directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private bool TryRead(string path, out RecoveryEntry? entry, out string reason)
    {
        entry = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxCheckpointBytes)
            {
                reason = "The checkpoint exceeds the size limit.";
                return false;
            }
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            bytes = UserDataProtection.Unprotect(bytes);
            var legacy = bytes.Length >= LegacyMagic.Length && bytes.AsSpan(0, LegacyMagic.Length).SequenceEqual(LegacyMagic);
            if (bytes.Length < Magic.Length + 4 || (!legacy && !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic)))
            {
                reason = "Unrecognised file header.";
                return false;
            }

            var headerLength = BitConverter.ToInt32(bytes, Magic.Length);
            var headerStart = Magic.Length + 4;
            if (headerLength <= 0 || headerLength > MaxHeaderBytes || headerLength > bytes.Length - headerStart)
            {
                reason = "Truncated header.";
                return false;
            }

            var header = JsonSerializer.Deserialize<Header>(bytes.AsSpan(headerStart, headerLength));
            if (header is null)
            {
                reason = "Unreadable header.";
                return false;
            }

            if (!TextEncodingIds.IsSupported(header.Encoding)
                || !Enum.IsDefined(header.DominantLineEnding)
                || (header.HasBom && !TextEncodingIds.SupportsBom(header.Encoding)))
            {
                reason = "Unsupported or invalid document format metadata.";
                return false;
            }
            if (header.SourceUrl is { } sourceUrl
                && (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source)
                    || (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps)
                    || string.IsNullOrEmpty(source.Host) || source.UserInfo.Length != 0))
            {
                reason = "Invalid source URL metadata.";
                return false;
            }

            var payloadStart = headerStart + headerLength;
            if (header.PayloadLength < 0 || header.PayloadLength > MaxPayloadBytes
                || header.PayloadLength != bytes.Length - payloadStart || (legacy && header.PayloadLength % 2 != 0))
            {
                reason = "Truncated payload.";
                return false;
            }

            var payload = bytes.AsSpan(payloadStart, header.PayloadLength);
            if (Convert.ToHexString(SHA256.HashData(payload)) != header.PayloadSha256)
            {
                reason = "Checksum mismatch.";
                return false;
            }

            var text = legacy ? new string(MemoryMarshal.Cast<byte, char>(payload)) : StrictUtf8.GetString(payload);
            if (text.Length > DocumentFileService.MaximumEagerLoadBytes)
            {
                reason = "The checkpoint text exceeds the size limit.";
                return false;
            }
            var format = new TextFileFormat(header.Encoding, header.HasBom, header.DominantLineEnding, header.HasMixedLineEndings, header.EndsWithNewline);
            var record = new RecoveryRecord(header.SessionId, header.OriginalPath, header.DisplayName, header.Revision, header.SavedAtUtc, header.BaselineSha256, format, text, header.IsExternalDraft, header.SourceUrl);
            entry = new RecoveryEntry(record, IsOwnerAlive(header.OwnerPid, header.OwnerStartTicks));
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or DecoderFallbackException)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static bool IsOwnerAlive(int pid, long startTicks)
    {
        if (startTicks == 0)
        {
            return false;
        }

        return SafeProcessStartTicks(pid) == startTicks;
    }

    private static long SafeProcessStartTicks(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    private int NextGeneration(Guid sessionId)
    {
        var existing = Directory.EnumerateFiles(_directory, $"{sessionId:N}.*{Extension}")
            .Select(ParseName)
            .Where(x => x is not null)
            .Select(x => x!.Value.Generation)
            .DefaultIfEmpty(0)
            .Max();
        return existing + 1;
    }

    private void PruneOldGenerations(Guid sessionId)
    {
        var files = Directory.EnumerateFiles(_directory, $"{sessionId:N}.*{Extension}")
            .Select(p => (Path: p, Parsed: ParseName(p)))
            .Where(x => x.Parsed is not null)
            .OrderByDescending(x => x.Parsed!.Value.Generation)
            .Skip(GenerationsToKeep);
        foreach (var file in files)
        {
            TryDelete(file.Path);
        }
    }

    private string FilePath(Guid sessionId, int generation) =>
        Path.Combine(_directory, $"{sessionId:N}.{generation:D8}{Extension}");

    private static (Guid SessionId, int Generation)? ParseName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('.');
        if (parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out var id) && int.TryParse(parts[1], out var gen))
        {
            return (id, gen);
        }

        return null;
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

    private sealed class Header
    {
        public Guid SessionId { get; set; }

        public string? OriginalPath { get; set; }
        public bool IsExternalDraft { get; set; }
        public string? SourceUrl { get; set; }

        public string DisplayName { get; set; } = string.Empty;

        public long Revision { get; set; }

        public DateTime SavedAtUtc { get; set; }

        public string? BaselineSha256 { get; set; }

        public TextEncodingKind Encoding { get; set; }

        public bool HasBom { get; set; }

        public LineEnding DominantLineEnding { get; set; }

        public bool HasMixedLineEndings { get; set; }

        public bool EndsWithNewline { get; set; }

        public int OwnerPid { get; set; }

        public long OwnerStartTicks { get; set; }

        public int PayloadLength { get; set; }

        public string PayloadSha256 { get; set; } = string.Empty;
    }
}
