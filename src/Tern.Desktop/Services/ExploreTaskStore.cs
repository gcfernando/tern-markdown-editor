using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tern.App.Knowledge;
using Tern.App.Shell;
using Tern.Filesystem;
using Tern.Workspaces;

namespace Tern.Desktop.Services;

internal sealed record ExploreNoteChoice(KnowledgeSource Source, bool Included);
internal sealed record ExploreReportCheckWitness(Guid TaskId, string ReportKey, string SourceKey, int PolicyVersion);

/// <summary>Only the unfinished current task. Credentials and provider conversation identifiers never belong here.</summary>
internal sealed record ExploreTaskCheckpoint
{
    public int Version { get; init; } = 1;
    public Guid TaskId { get; init; } = Guid.NewGuid();
    public string WorkspaceRoot { get; init; } = "";
    public string Question { get; init; } = "";
    public string Operation { get; init; } = "Answer question";
    public int Stage { get; init; }
    public ExploreNoteChoice[] Choices { get; init; } = [];
    public string[] AddedNotes { get; init; } = [];
    public KnowledgeTurn? Answer { get; init; }
    public KnowledgeSource[] AnswerSources { get; init; } = [];
    public KnowledgeReportDraft? Draft { get; init; }
    public KnowledgeReportDraft? RetainedDraft { get; init; }
    public string ReportMaterial { get; init; } = "";
    public string ReportCoverage { get; init; } = "";
    public string[] ReportIssues { get; init; } = [];
    public string? ReportIssuesKey { get; init; }
    public KnowledgeCachedNote[] CachedNotes { get; init; } = [];
    public KnowledgeCachedCoverage? CachedCoverage { get; init; }
    public Dictionary<string, KnowledgeSourceState> SourceStates { get; init; } = new(WorkspacePaths.Comparer);
    public string? CheckedContentKey { get; init; }
    public ExploreReportCheckWitness? CheckWitness { get; init; }
    public string Output { get; init; } = "email";
    public string Status { get; init; } = "";
    public string NotFoundReason { get; init; } = "";
    public bool Interrupted { get; init; }
    public string? RetryStep { get; init; }
    public bool InsertionPending { get; init; }
    public bool NeedsRefresh { get; init; }
    public bool HasPending => !string.IsNullOrWhiteSpace(Question) || Choices is { Length: > 0 } || Answer is not null || Draft is not null || RetainedDraft is not null || Interrupted || RetryStep is not null || InsertionPending;

    public bool ValidFor(string root)
    {
        try
        {
            if (WorkspaceRoot is null || Question is null || Operation is null || Choices is null || AnswerSources is null || AddedNotes is null || ReportMaterial is null || ReportCoverage is null || ReportIssues is null || CachedNotes is null || SourceStates is null || Status is null || NotFoundReason is null
                || Version != 1 || TaskId == Guid.Empty || Stage is < 0 or > 4 || Question.Length > 1500 || Operation.Length > 120
                || !WorkspacePaths.Comparer.Equals(WorkspacePaths.Normalize(WorkspaceRoot), WorkspacePaths.Normalize(root))
                || Output is not ("email" or "pdf" or "note" or "print") || RetryStep is not (null or "find" or "answer" or "share" or "finish" or "revise")) return false;
            if (ReportIssues.Any(issue => string.IsNullOrWhiteSpace(issue)) || ReportIssues.Length > 0 && (Draft is null || ReportIssuesKey is not { Length: 64 } || ReportIssuesKey.Any(character => !Uri.IsHexDigit(character)))) return false;
            if (Answer is not null && (Answer.Question is null || Answer.Answer is null || Answer.Answer.Answer is null || Answer.Answer.Interpretation is null
                || Answer.Answer.Citations is null || Answer.Answer.Citations.Any(citation => citation is null || citation.Reason is null))) return false;
            if (!ValidDraft(Draft) || !ValidDraft(RetainedDraft)) return false;
            bool Inside(string path) => Path.IsPathFullyQualified(path) && WorkspacePaths.IsUnder(root, path);
            if (CachedNotes.Any(note => note is null || note.Text is null || note.Source is null || !AnswerSources.Any(source => source.Id == note.Source.Id
                && WorkspacePaths.PathsEqual(source.Path, note.Source.Path) && source.ContentDigest == note.Source.ContentDigest))
                || CachedNotes.Sum(note => (long)note.Text.Length) > KnowledgeLimits.MaximumQuestionCharacters
                || SourceStates.Any(item => !Inside(item.Key) || item.Value is null || item.Value.PhysicalPath is null)) return false;
            if (CheckedContentKey is not null && (CheckedContentKey.Length != 64 || CheckedContentKey.Any(character => !Uri.IsHexDigit(character)) || Draft is null || CachedNotes.Length == 0)) return false;
            var sources = Choices.Select(choice => choice.Source).Concat(AnswerSources).Concat(Answer?.Answer.Citations.Select(c => c.Source) ?? []);
            if (sources.Any(source => source is null || source.Path is null || source.RelativePath is null || source.WorkspaceRoot is null || source.Title is null || source.Excerpt is null || source.ContentDigest is null || source.Tags is null
                || source.Tags.Any(tag => tag is null) || source.Id < 1 || source.Line < 1 || !Inside(source.Path) || Path.IsPathRooted(source.RelativePath)
                || !WorkspacePaths.Comparer.Equals(WorkspacePaths.Normalize(source.WorkspaceRoot), WorkspacePaths.Normalize(root))
                || !WorkspacePaths.Comparer.Equals(Path.GetFullPath(Path.Combine(root, source.RelativePath)), Path.GetFullPath(source.Path)))) return false;
            if (Choices.Select(c => c.Source.Id).Distinct().Count() != Choices.Length || AddedNotes.Any(path => !Inside(path))) return false;
            if (AnswerSources.Select(source => source.Id).Distinct().Count() != AnswerSources.Length) return false;
            if (Answer is not null && Answer.Answer.Citations.Any(citation => !AnswerSources.Any(source => source.Id == citation.Source.Id
                && WorkspacePaths.PathsEqual(source.Path, citation.Source.Path) && source.ContentDigest == citation.Source.ContentDigest))) return false;
            if (Stage == 1 && Choices.Length == 0 || Stage is 2 or 4 && Answer is null || Stage == 4 && Draft is null) return false;
            if (Answer is not null && (!string.Equals(Answer.Question.Trim(), Question.Trim(), StringComparison.Ordinal) || Answer.Answer.InsufficientEvidence)) return false;
            return HasPending;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or NullReferenceException or JsonException) { return false; }
    }

    internal static string ReportKey(Tern.Export.DecisionReport report) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(report)));

    private static bool ValidDraft(KnowledgeReportDraft? draft)
    {
        if (draft is null) return true;
        if (draft.Report is null || draft.NumbersToCheck is null || draft.NumbersToCheck.Any(value => value is null)) return false;
        // Report fields and list items are non-null by contract. Only an optional source line may be null.
        static bool Valid(JsonElement element, string? property = null) => element.ValueKind switch
        {
            JsonValueKind.Null => property == "Line",
            JsonValueKind.Object => element.EnumerateObject().All(item => Valid(item.Value, item.Name)),
            JsonValueKind.Array => element.EnumerateArray().All(item => Valid(item)),
            _ => true,
        };
        return Valid(JsonSerializer.SerializeToElement(draft.Report));
    }
}

/// <summary>Private work stays in this shell's memory. Other checkpoints are atomic CurrentUser-protected files.</summary>
internal sealed class ExploreTaskStore(string directory)
{
    private static readonly ConditionalWeakTable<ShellViewModel, ExploreTaskStore> Stores = new();
    private readonly Dictionary<string, ExploreTaskCheckpoint> _memory = new(WorkspacePaths.Comparer);
    private readonly HashSet<string> _cleared = new(WorkspacePaths.Comparer);
    private string? _lastRoot;
    public static ExploreTaskStore For(ShellViewModel shell) => Stores.GetValue(shell, value =>
    {
        var store = new ExploreTaskStore(value.Paths.SettingsDirectory);
        store.ObserveWorkspacePrivacy(value);
        return store;
    });
    public string? Problem { get; private set; }
    public WorkspaceSession? ResumeWorkspace(IEnumerable<WorkspaceSession> workspaces)
    {
        var available = workspaces.ToArray();
        var recent = available.FirstOrDefault(root => _lastRoot is not null && WorkspacePaths.PathsEqual(root.Root, _lastRoot));
        if (recent is not null && Load(recent.Root, recent.IsPrivate)?.HasPending == true) return recent;
        return available.Where(root => Load(root.Root, root.IsPrivate)?.HasPending == true)
            .OrderByDescending(root => File.Exists(FileFor(root.Root)) ? File.GetLastWriteTimeUtc(FileFor(root.Root)) : DateTime.MinValue).FirstOrDefault();
    }
    internal string FileFor(string root)
    {
        var normalized = WorkspacePaths.Normalize(root);
        if (WorkspacePaths.IsCaseInsensitive) normalized = normalized.ToUpperInvariant();
        return Path.Combine(directory, "explore-pending", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))) + ".bin");
    }

    public ExploreTaskCheckpoint? Load(string root, bool privateWorkspace)
    {
        if (privateWorkspace) RemoveDisk(root);
        if (_cleared.Contains(root)) return null;
        if (_memory.TryGetValue(root, out var current)) return current.ValidFor(root) ? current : null;
        if (privateWorkspace) { RemoveDisk(root); return null; }
        try
        {
            var file = FileFor(root);
            EnsureStoragePath(file);
            if (!File.Exists(file)) return null;
            if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new IOException("The saved Explore task is too large to reopen safely.");
            var bytes = File.ReadAllBytes(file);
            // This format has no plaintext legacy. Unprotect's legacy fallback must never apply here.
            if (!bytes.AsSpan().StartsWith("TERNDP01"u8)) throw new IOException("The saved Explore task is not protected.");
            var task = JsonSerializer.Deserialize<ExploreTaskCheckpoint>(UserDataProtection.Unprotect(bytes));
            if (task is null || !task.ValidFor(root)) throw new IOException("The saved Explore task could not be checked against this folder.");
            _memory[root] = task;
            return task;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Problem = "Tern could not reopen the saved Explore task. Start a new question. " + ex.Message; return null; }
    }

    public void Save(ExploreTaskCheckpoint task, bool privateWorkspace)
    {
        if (!task.HasPending) { Remove(task.WorkspaceRoot); return; }
        if (!task.ValidFor(task.WorkspaceRoot))
        { Problem = "Tern could not save this Explore task safely. Your last saved task is kept; keep this window open to retain your current wording."; return; }
        _memory[task.WorkspaceRoot] = task;
        _lastRoot = task.WorkspaceRoot;
        _cleared.Remove(task.WorkspaceRoot);
        if (privateWorkspace) { RemoveDisk(task.WorkspaceRoot); return; }
        string? temporary = null;
        try
        {
            var bytes = UserDataProtection.Protect(JsonSerializer.SerializeToUtf8Bytes(task));
            if (bytes.Length > 16 * 1024 * 1024) throw new IOException("The unfinished task is too large for a saved checkpoint.");
            var file = FileFor(task.WorkspaceRoot);
            EnsureStoragePath(file);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            EnsureStoragePath(file);
            temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            EnsureStoragePath(file);
            File.Move(temporary, file, overwrite: true);
            Problem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { RemoveDisk(task.WorkspaceRoot); Problem = "Your Explore task is kept for this open Tern session only. Tern could not protect a saved copy; keep Tern open to retain it."; }
        finally
        {
            try { if (temporary is not null) { EnsureStoragePath(temporary); if (File.Exists(temporary)) File.Delete(temporary); } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Remove(string root) { _memory.Remove(root); _cleared.Add(root); RemoveDisk(root); }
    private bool RemoveDisk(string root)
    {
        try
        {
            var file = FileFor(root); EnsureStoragePath(file);
            if (File.Exists(file)) File.Delete(file);
            var folder = Path.GetDirectoryName(file)!;
            if (Directory.Exists(folder))
            {
                var prefix = Path.GetFileName(file) + ".";
                foreach (var temporary in Directory.EnumerateFiles(folder, prefix + "*.tmp", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(temporary);
                    var suffix = name[prefix.Length..^4];
                    if (suffix.Length != 32 || !Guid.TryParseExact(suffix, "N", out _)) continue;
                    EnsureStoragePath(temporary);
                    File.Delete(temporary);
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Problem = "Tern could not remove the older saved Explore task. It will stay closed in this Tern session, but the old copy may reappear after restarting Tern. Check access to Tern's settings folder."; return false; }
    }

    private void ObserveWorkspacePrivacy(ShellViewModel shell)
    {
        void Purge(WorkspaceSession workspace)
        {
            if (!workspace.IsPrivate) return;
            // Privacy conversion replaces the workspace object. Retain the current job in
            // this shell's memory before removing its formerly permitted disk checkpoint.
            _ = Load(workspace.Root, privateWorkspace: false);
            if (!RemoveDisk(workspace.Root))
            {
                const string warning = "Private mode is on, but an older saved Explore task could not be removed. Your unfinished task stays in memory. Check access to Tern's settings folder, then reopen Explore to retry removing the saved copy.";
                shell.ShowBanner(warning, StatusSeverity.Warning);
                shell.SetStatus(warning, StatusSeverity.Warning);
            }
        }
        shell.OpenWorkspaces.CollectionChanged += (_, change) =>
        {
            if (change.NewItems is { } added)
                foreach (var workspace in added.OfType<WorkspaceSession>()) Purge(workspace);
        };
        foreach (var workspace in shell.OpenWorkspaces) Purge(workspace);
    }

    private void EnsureStoragePath(string file)
    {
        var boundary = Path.GetFullPath(directory);
        var target = Path.GetFullPath(file);
        if (!WorkspacePaths.IsUnder(boundary, target)) throw new IOException("The saved Explore task is outside Tern's settings folder.");
        for (string? item = target; item is not null && WorkspacePaths.IsSameOrUnder(boundary, item); item = Path.GetDirectoryName(item))
        {
            try
            {
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A linked saved-task location cannot be used safely.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (WorkspacePaths.PathsEqual(item, boundary)) break;
        }
    }
}
