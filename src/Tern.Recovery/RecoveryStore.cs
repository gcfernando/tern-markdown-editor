using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tern.Domain.Text;

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
    private static readonly byte[] Magic = "TERNREC1"u8.ToArray();
    private const int GenerationsToKeep = 2;
    private const int MaxCheckpointBytes = 128 * 1024 * 1024;
    private const int MaxHeaderBytes = 1024 * 1024;
    private const string Extension = ".tern-recovery";
    private readonly string _directory;
    private readonly int _ownerPid = Environment.ProcessId;
    private readonly long _ownerStartTicks = SafeProcessStartTicks(Environment.ProcessId);

    // Actual persistence boundaries for deterministic fault/crash tests. Null in the application.
    internal Action<string>? CheckpointPhase { get; set; }

    public RecoveryStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tern", "recovery");

    public async Task WriteCheckpointAsync(RecoveryRecord record, CancellationToken cancellationToken = default)
    {
        var generation = NextGeneration(record.SessionId);
        var path = FilePath(record.SessionId, generation);
        var tempPath = path + ".tmp";

        var payload = MemoryMarshal.AsBytes(record.Text.AsSpan()).ToArray();
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
            OwnerPid = _ownerPid,
            OwnerStartTicks = _ownerStartTicks,
            PayloadLength = payload.Length,
            PayloadSha256 = Convert.ToHexString(SHA256.HashData(payload)),
        };
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header);
        if (headerBytes.Length > MaxHeaderBytes || (long)Magic.Length + 4 + headerBytes.Length + payload.Length > MaxCheckpointBytes)
        {
            throw new IOException("The recovery checkpoint exceeds the 128 MiB limit.");
        }

        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        {
            await stream.WriteAsync(Magic, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(BitConverter.GetBytes(headerBytes.Length), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Removes all checkpoints for a session once its content is safely on disk or deliberately discarded.</summary>
    public void Retire(Guid sessionId)
    {
        foreach (var path in Directory.EnumerateFiles(_directory, $"{sessionId:N}.*{Extension}"))
        {
            TryDelete(path);
        }
    }

    public void DeleteFile(string path) => TryDelete(path);

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
            if (bytes.Length < Magic.Length + 4 || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
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
            if (header.PayloadLength < 0 || header.PayloadLength != bytes.Length - payloadStart || header.PayloadLength % 2 != 0)
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

            var text = new string(MemoryMarshal.Cast<byte, char>(payload));
            var format = new TextFileFormat(header.Encoding, header.HasBom, header.DominantLineEnding, header.HasMixedLineEndings, header.EndsWithNewline);
            var record = new RecoveryRecord(header.SessionId, header.OriginalPath, header.DisplayName, header.Revision, header.SavedAtUtc, header.BaselineSha256, format, text, header.IsExternalDraft, header.SourceUrl);
            entry = new RecoveryEntry(record, IsOwnerAlive(header.OwnerPid, header.OwnerStartTicks));
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
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
