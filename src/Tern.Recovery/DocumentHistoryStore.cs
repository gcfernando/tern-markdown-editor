using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tern.Filesystem;

namespace Tern.Recovery;

/// <summary>One saved version. <paramref name="Label"/> explains why a version was captured outside an ordinary save.</summary>
public sealed record DocumentVersion(DateTime SavedAtUtc, string Text, string Sha256, string? Label = null)
{
    public override string ToString() => $"{SavedAtUtc.ToLocalTime():g} · {Text.Length:N0} characters";
}

/// <summary>A history entry that failed its integrity check. It is never restored, only reported.</summary>
public sealed record DamagedVersion(DateTime? SavedAtUtc, string FileName, string Reason);

public sealed record HistoryReadResult(IReadOnlyList<DocumentVersion> Versions, IReadOnlyList<DamagedVersion> Damaged);

/// <summary>Outcome of a capture: whether a new version was stored and how many damaged entries were set aside.</summary>
public sealed record HistoryCaptureResult(bool Added, int DamagedSetAside, bool HistoryWasEmpty);

/// <summary>Local saved-text history, separate from crash recovery; age-thinned within a per-file byte budget.</summary>
public sealed class DocumentHistoryStore(string root, long byteBudget = DocumentHistoryStore.DefaultByteBudget, Func<DateTime>? clock = null,
    bool protectCopies = false, Func<string, bool>? allowPersistence = null) : IDisposable
{
    public const long DefaultByteBudget = 32 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long ByteBudget = Math.Max(1024 * 1024, byteBudget);
    public long ByteBudgetPerFile => ByteBudget;
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    /// <summary>Versions kept from the last hour; older versions are thinned to one per hour, then per day, then per week.</summary>
    private const int VersionLimit = 30;
    private const int MaxRetained = 200;
    private const string TrashFolder = ".trash";
    private const string TrashMarker = "trashed.json";
    /// <summary>How long history of a deleted file stays restorable.</summary>
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);
    private const int DamagedLimit = 10;
    private const string DamagedSuffix = ".corrupt";
    private const string StaleSuffix = ".stale";
    private const string IdentityFile = "identity.origin";
    private static readonly System.Text.RegularExpressions.Regex OwnedFile = new(
        @"^\d{19}-[a-f0-9]{32}(?:-[a-f0-9]{32})*\.json(?:\.corrupt)?(?:\.stale)?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static long _lastTicks;

    /// <summary>Which file a history folder belongs to: its path and the creation time of the file when the history began.</summary>
    private sealed record Origin(string Path, long Created);

    private enum Ownership { Unknown, Match, Mismatch }

    /// <summary>
    /// A history folder belongs to one file, not just one path. A different file created later at the same path
    /// (deleted and recreated outside Tern) must not inherit it. Folders from before this check are compared by age.
    /// </summary>
    private Ownership Evaluate(string directory, string path, out long? created)
    {
        created = FileIdentity.TryGetCreationTicks(path);
        if (created is null || !Directory.Exists(directory)) return Ownership.Unknown;
        if (ReadOrigin(directory) is { } origin) return origin.Created == created ? Ownership.Match : Ownership.Mismatch;
        var newest = new DirectoryInfo(directory).EnumerateFiles("*.json").Select(f => TimestampFromName(f.Name)).Max();
        return newest is { } saved && saved.Ticks < created.Value - TimeSpan.FromMinutes(2).Ticks ? Ownership.Mismatch : Ownership.Match;
    }

    private static Origin? ReadOrigin(string directory)
    {
        try
        {
            var file = Path.Combine(directory, IdentityFile);
            return File.Exists(file) ? JsonSerializer.Deserialize<Origin>(File.ReadAllBytes(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static void WriteOrigin(string directory, string path, long created)
    {
        try
        {
            var file = Path.Combine(directory, IdentityFile);
            var temporary = file + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new Origin(Path.GetFullPath(path), created)));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Hides every entry of a folder that belongs to another file. The entries are swept after the retention period.</summary>
    private static void SetAsideStale(string directory)
    {
        var now = DateTime.UtcNow;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles().Where(f => OwnedFile.IsMatch(f.Name) && !f.Name.EndsWith(StaleSuffix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            File.Move(file.FullName, file.FullName + StaleSuffix, overwrite: true);
            File.SetLastWriteTimeUtc(file.FullName + StaleSuffix, now);
        }
        var origin = Path.Combine(directory, IdentityFile);
        if (File.Exists(origin)) File.Delete(origin);
    }

    /// <summary>Stores <paramref name="text"/> unless it equals the newest valid version. Damaged newest entries are set aside.</summary>
    public async Task<HistoryCaptureResult> CaptureAsync(string path, string text, string? label = null, string? labelWhenHistoryEmpty = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (allowPersistence?.Invoke(path) == false) return new(false, 0, true);
            return await Task.Run(() => CaptureCoreAsync(path, text, label, labelWhenHistoryEmpty)).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<HistoryCaptureResult> CaptureCoreAsync(string path, string text, string? label, string? labelWhenHistoryEmpty)
    {
        await TryRestoreFromTrashAsync(path, text).ConfigureAwait(false);
        var directory = DirectoryFor(path);
        Directory.CreateDirectory(directory);
        if (Evaluate(directory, path, out var created) == Ownership.Mismatch) SetAsideStale(directory);
        if (created is { } ownerCreated) WriteOrigin(directory, path, ownerCreated);
        var digest = Hash(text);
        // Saving needs only the newest valid version for deduplication. Entries that fail their integrity
        // check (torn writes, tampering) are set aside so one bad file cannot disable history.
        var damaged = 0;
        var anyValid = false;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal))
        {
            var (version, _) = await TryReadVersionAsync(file).ConfigureAwait(false);
            if (version is null)
            {
                Quarantine(file);
                damaged++;
                continue;
            }
            anyValid = true;
            if (version.Sha256 == digest) return new HistoryCaptureResult(false, damaged, false);
            break;
        }

        label = !anyValid && labelWhenHistoryEmpty is not null ? labelWhenHistoryEmpty : label;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new DocumentVersion(DateTime.UtcNow, text, digest, label));
        if (protectCopies) payload = UserDataProtection.Protect(payload);
        if (payload.LongLength > ByteBudget) throw new IOException(
            $"Version history is unavailable for this document: one version exceeds the {ByteBudget / (1024 * 1024)} MiB local-history budget. Increase Version history size in Settings. The document save and recovery checkpoints are separate.");
        var target = Path.Combine(directory, $"{NextTicks():D19}-{Guid.NewGuid():N}.json");
        var temporary = target + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target);
        }
        finally
        {
            File.Delete(temporary);
        }

        Prune(directory);
        return new HistoryCaptureResult(true, damaged, !anyValid);
    }

    /// <summary>Returns valid versions only; damaged entries are skipped. Use <see cref="ReadWithDamageAsync"/> to see them.</summary>
    public async Task<IReadOnlyList<DocumentVersion>> ReadAsync(string path) => (await ReadWithDamageAsync(path).ConfigureAwait(false)).Versions;

    public async Task<HistoryReadResult> ReadWithDamageAsync(string path)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await ReadCoreAsync(path).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private static long NextTicks()
    {
        while (true)
        {
            var seen = Interlocked.Read(ref _lastTicks);
            var next = Math.Max(DateTime.UtcNow.Ticks, seen + 1);
            if (Interlocked.CompareExchange(ref _lastTicks, next, seen) == seen) return next;
        }
    }

    /// <summary>
    /// Thins versions by age so frequent saving cannot evict older history: up to <see cref="VersionLimit"/> from the last hour,
    /// then the newest per hour for a day, per day for 30 days and per week beyond that, all inside the byte budget.
    /// </summary>
    private void Prune(string directory)
    {
        var now = _clock();
        var seen = new HashSet<(int Tier, long Bucket)>();
        var recent = 0;
        long bytes = 0;
        var kept = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal))
        {
            var age = now - (TimestampFromName(file.Name) ?? now);
            bool keep;
            if (age < TimeSpan.FromHours(1)) keep = ++recent <= VersionLimit;
            else if (age < TimeSpan.FromHours(24)) keep = seen.Add((1, (long)age.TotalHours));
            else if (age < TimeSpan.FromDays(30)) keep = seen.Add((2, (long)age.TotalDays));
            else keep = seen.Add((3, (long)(age.TotalDays / 7)));
            if (keep)
            {
                bytes += file.Length;
                keep = ++kept <= MaxRetained && (bytes <= ByteBudget || kept == 1);
            }
            if (!keep) file.Delete();
        }
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json" + DamagedSuffix).OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(DamagedLimit))
            file.Delete();
    }

    /// <summary>Parses and verifies one entry; (null, reason) when it is unreadable. I/O failures still propagate.</summary>
    private async Task<(DocumentVersion? Version, string? Reason)> TryReadVersionAsync(FileInfo file)
    {
        if (file.Length > ByteBudget) return (null, "The entry exceeds the local-history size budget.");
        var content = await File.ReadAllBytesAsync(file.FullName).ConfigureAwait(false);
        try
        {
            var version = JsonSerializer.Deserialize<DocumentVersion>(UserDataProtection.Unprotect(content));
            if (version is null || version.Text is null || version.Sha256 is null) return (null, "The entry is empty or incomplete.");
            return Hash(version.Text) != version.Sha256 ? (null, "The entry failed its integrity check.") : (version, null);
        }
        catch (JsonException)
        {
            return (null, "The entry could not be parsed.");
        }
        catch (IOException ex)
        {
            return (null, ex.Message);
        }
    }

    private static void Quarantine(FileInfo file)
    {
        var target = file.FullName + DamagedSuffix;
        if (File.Exists(target)) target = file.FullName[..^".json".Length] + "-" + Guid.NewGuid().ToString("N") + ".json" + DamagedSuffix;
        File.Move(file.FullName, target);
    }

    private static DateTime? TimestampFromName(string name)
    {
        if (name.Length >= 19 && long.TryParse(name.AsSpan(0, 19), out var ticks) && ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
            return new DateTime(ticks, DateTimeKind.Utc);
        return null;
    }

    /// <summary>Removes only application-owned saved versions for one deleted namespace path.</summary>
    public async Task PurgeAsync(string path)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var directory in new[] { DirectoryFor(path), TrashDirectoryFor(path) })
            {
                if (!Directory.Exists(directory)) continue;
                for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
                    if (ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        throw new IOException("Saved-text history cannot be purged through a linked directory.");
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (!OwnedFile.IsMatch(Path.GetFileName(file)) && Path.GetFileName(file) is not (IdentityFile or TrashMarker)) continue;
                    if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                        throw new IOException("A saved-text history entry is a link; it was not removed.");
                    File.Delete(file);
                }
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task PurgeWorkspaceAsync(string workspaceRoot)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(root)) return;
            for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("History cannot be cleared through a linked directory.");
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var directories = Directory.EnumerateDirectories(root).Where(directory => Path.GetFileName(directory) != TrashFolder).ToList();
            var trash = Path.Combine(root, TrashFolder);
            if (Directory.Exists(trash))
            {
                if (File.GetAttributes(trash).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Recycled history storage is a linked directory.");
                directories.AddRange(Directory.EnumerateDirectories(trash));
            }
            foreach (var directory in directories)
            {
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("History cannot be cleared through a linked directory.");
                var marker = Path.Combine(directory, TrashMarker);
                foreach (var metadata in new[] { marker, Path.Combine(directory, IdentityFile) })
                    if (File.Exists(metadata) && File.GetAttributes(metadata).HasFlag(FileAttributes.ReparsePoint))
                        throw new IOException("History origin metadata is a link; it was not followed.");
                var path = ReadOrigin(directory)?.Path;
                if (path is null && File.Exists(marker)) path = JsonSerializer.Deserialize<TrashInfo>(File.ReadAllBytes(marker))?.Path;
                if (path is null)
                {
                    if (Directory.EnumerateFiles(directory).Any(file => OwnedFile.IsMatch(Path.GetFileName(file))))
                        throw new IOException("Some older history has unreadable origin metadata and cannot be associated with a workspace. Clear those versions by opening their original file; unassociated copies were kept.");
                    continue;
                }
                if (!Path.GetFullPath(path).StartsWith(prefix, comparison)) continue;
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (!OwnedFile.IsMatch(Path.GetFileName(file)) && Path.GetFileName(file) is not (IdentityFile or TrashMarker)) continue;
                    if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                        throw new IOException("A history entry is a link; it was not removed.");
                    File.Delete(file);
                }
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Moves the saved-text history of a file that was recycled into <c>.trash</c>, where it is kept for <see cref="TrashRetention"/>
    /// and merged back automatically when the same path reappears with the content of the newest kept version.
    /// </summary>
    public async Task<bool> TrashAsync(string path)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = DirectoryFor(path);
            if (!Directory.Exists(directory)) return false;
            for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Saved-text history cannot be moved through a linked directory.");
            var trash = TrashDirectoryFor(path);
            Directory.CreateDirectory(trash);
            var moved = MoveVersions(directory, trash);
            File.WriteAllBytes(Path.Combine(trash, TrashMarker), JsonSerializer.SerializeToUtf8Bytes(new TrashInfo(Path.GetFullPath(path), _clock())));
            var origin = Path.Combine(directory, IdentityFile);
            if (File.Exists(origin)) File.Delete(origin);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            return moved > 0;
        }
        finally { _gate.Release(); }
    }

    /// <summary>True when recycled history is waiting for this path.</summary>
    public bool HasTrashedHistory(string path) => Directory.Exists(TrashDirectoryFor(path));

    private sealed record TrashInfo(string Path, DateTime TrashedUtc);

    private string TrashDirectoryFor(string path) => Path.Combine(root, TrashFolder, Path.GetFileName(DirectoryFor(path)));

    private static int MoveVersions(string source, string destination)
    {
        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(source).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json" + DamagedSuffix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var name = Path.GetFileName(file);
            var target = Path.Combine(destination, name);
            if (File.Exists(target))
            {
                var suffix = name.EndsWith(DamagedSuffix, StringComparison.OrdinalIgnoreCase) ? ".json" + DamagedSuffix : ".json";
                target = Path.Combine(destination, name[..^suffix.Length] + "-" + Guid.NewGuid().ToString("N") + suffix);
            }
            File.Move(file, target, overwrite: false);
            moved++;
        }
        return moved;
    }

    private static bool SameText(string a, string b) =>
        string.Equals(a, b, StringComparison.Ordinal) || string.Equals(a.Replace("\r\n", "\n"), b.Replace("\r\n", "\n"), StringComparison.Ordinal);

    /// <summary>Restores recycled history when the file at <paramref name="path"/> matches the newest version that was kept.</summary>
    private async Task<bool> TryRestoreFromTrashAsync(string path, string? knownText)
    {
        try
        {
            var trash = TrashDirectoryFor(path);
            if (!Directory.Exists(trash)) return false;
            var markerFile = Path.Combine(trash, TrashMarker);
            var trashedAt = File.Exists(markerFile) ? JsonSerializer.Deserialize<TrashInfo>(File.ReadAllBytes(markerFile))?.TrashedUtc : null;
            if (_clock() - (trashedAt ?? Directory.GetLastWriteTimeUtc(trash)) > TrashRetention) return false;
            DocumentVersion? newest = null;
            foreach (var file in new DirectoryInfo(trash).EnumerateFiles("*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal))
            {
                (newest, _) = await TryReadVersionAsync(file).ConfigureAwait(false);
                if (newest is not null) break;
            }
            if (newest is null) return false;
            var text = knownText ?? (File.Exists(path) ? (await new DocumentFileService().LoadAsync(path).ConfigureAwait(false)).Text : null);
            if (text is null || !SameText(text, newest.Text)) return false;
            var destination = DirectoryFor(path);
            Directory.CreateDirectory(destination);
            MoveVersions(trash, destination);
            if (File.Exists(markerFile)) File.Delete(markerFile);
            File.Delete(Path.Combine(destination, IdentityFile));
            if (FileIdentity.TryGetCreationTicks(path) is { } created) WriteOrigin(destination, path, created);
            if (!Directory.EnumerateFileSystemEntries(trash).Any()) Directory.Delete(trash);
            Prune(destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    /// <summary>Deletes recycled history older than <see cref="TrashRetention"/>.</summary>
    private int SweepTrash(DateTime nowUtc)
    {
        var trashRoot = Path.Combine(root, TrashFolder);
        if (!Directory.Exists(trashRoot)) return 0;
        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(trashRoot).ToList())
        {
            try
            {
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue;
                var markerFile = Path.Combine(directory, TrashMarker);
                DateTime trashedAt;
                try { trashedAt = File.Exists(markerFile) ? JsonSerializer.Deserialize<TrashInfo>(File.ReadAllBytes(markerFile))?.TrashedUtc ?? DateTime.MinValue : Directory.GetLastWriteTimeUtc(directory); }
                catch (JsonException) { trashedAt = Directory.GetLastWriteTimeUtc(directory); }
                if (nowUtc - trashedAt <= TrashRetention) continue;
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles().Where(f => OwnedFile.IsMatch(f.Name) && !f.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList())
                { file.Delete(); removed++; }
                if (File.Exists(markerFile)) File.Delete(markerFile);
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (!Directory.EnumerateFileSystemEntries(trashRoot).Any()) Directory.Delete(trashRoot);
        return removed;
    }

    /// <summary>
    /// Removes history that can no longer be reached: entries hidden because another file replaced theirs, and folders whose
    /// file is gone (or was replaced) and whose newest version is older than <paramref name="maxAge"/>. Returns the number of entries removed.
    /// </summary>
    public async Task<int> SweepAsync(TimeSpan maxAge, DateTime? nowUtc = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(root)) return 0;
            var cutoff = (nowUtc ?? DateTime.UtcNow) - maxAge;
            var removed = SweepTrash(nowUtc ?? DateTime.UtcNow);
            foreach (var directory in Directory.EnumerateDirectories(root).Where(d => !string.Equals(Path.GetFileName(d), TrashFolder, StringComparison.Ordinal)).ToList())
            {
                try
                {
                    if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue;
                    var files = new DirectoryInfo(directory).EnumerateFiles().Where(f => OwnedFile.IsMatch(f.Name) && !f.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList();
                    foreach (var stale in files.Where(f => f.Name.EndsWith(StaleSuffix, StringComparison.OrdinalIgnoreCase) && f.LastWriteTimeUtc < cutoff))
                    { stale.Delete(); removed++; }
                    if (ReadOrigin(directory) is { } origin)
                    {
                        var live = files.Where(f => !f.Name.EndsWith(StaleSuffix, StringComparison.OrdinalIgnoreCase) && f.Exists).ToList();
                        var orphaned = FileIdentity.TryGetCreationTicks(origin.Path) != origin.Created;
                        if (orphaned && live.All(f => f.LastWriteTimeUtc < cutoff))
                        {
                            foreach (var file in live) { file.Delete(); removed++; }
                            File.Delete(Path.Combine(directory, IdentityFile));
                        }
                    }
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return removed;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Moves saved-text history with a renamed document, retaining any earlier destination history.</summary>
    public async Task RenameAsync(string oldPath, string newPath)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var source = DirectoryFor(oldPath);
            var destination = DirectoryFor(newPath);
            // Damaged entries move with the history so they stay visible as damaged instead of vanishing.
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(source)) return;
            // History that never belonged to the file now at the new path is not carried along.
            if (Evaluate(source, newPath, out var created) == Ownership.Mismatch) return;
            if (!Directory.Exists(destination))
            {
                Directory.Move(source, destination);
                if (created is { } moved) WriteOrigin(destination, newPath, moved);
                return;
            }
            if (Evaluate(destination, newPath, out _) == Ownership.Mismatch) SetAsideStale(destination);
            foreach (var file in Directory.EnumerateFiles(source).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json" + DamagedSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                var target = Path.Combine(destination, Path.GetFileName(file));
                if (File.Exists(target))
                {
                    var name = Path.GetFileName(file);
                    var suffix = name.EndsWith(DamagedSuffix, StringComparison.OrdinalIgnoreCase) ? ".json" + DamagedSuffix : ".json";
                    target = Path.Combine(destination, name[..^suffix.Length] + "-" + Guid.NewGuid().ToString("N") + suffix);
                }
                File.Move(file, target, overwrite: false);
            }
            var sourceOrigin = Path.Combine(source, IdentityFile);
            if (File.Exists(sourceOrigin)) File.Delete(sourceOrigin);
            // Unknown files are retained; never recursively remove a history directory.
            if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
            if (created is { } merged) WriteOrigin(destination, newPath, merged);
            Prune(destination);
        }
        finally { _gate.Release(); }
    }

    private async Task<HistoryReadResult> ReadCoreAsync(string path)
    {
        await TryRestoreFromTrashAsync(path, null).ConfigureAwait(false);
        var directory = DirectoryFor(path);
        if (!Directory.Exists(directory)) return new HistoryReadResult([], []);
        var ownership = Evaluate(directory, path, out var created);
        // Another file now lives at this path: its history is hidden here and swept later.
        if (ownership == Ownership.Mismatch) return new HistoryReadResult([], []);
        if (ownership == Ownership.Match && created is { } adopted && ReadOrigin(directory) is null) WriteOrigin(directory, path, adopted);
        var result = new List<DocumentVersion>();
        var damaged = new List<DamagedVersion>();
        long bytes = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal).Take(MaxRetained))
        {
            bytes += file.Length;
            if (bytes > ByteBudget) break;
            var (version, reason) = await TryReadVersionAsync(file).ConfigureAwait(false);
            if (version is null) damaged.Add(new DamagedVersion(TimestampFromName(file.Name), file.Name, reason!));
            else result.Add(version);
        }
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json" + DamagedSuffix).OrderByDescending(f => f.Name, StringComparer.Ordinal).Take(DamagedLimit))
            damaged.Add(new DamagedVersion(TimestampFromName(file.Name), file.Name, "The entry was damaged and has been set aside."));
        damaged.Sort((a, b) => Nullable.Compare(b.SavedAtUtc, a.SavedAtUtc));
        return new HistoryReadResult(result, damaged);
    }

    private string DirectoryFor(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
        return Path.Combine(root, Hash(full));
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public void Dispose() => _gate.Dispose();
}
