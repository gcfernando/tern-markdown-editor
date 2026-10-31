using CommunityToolkit.Mvvm.ComponentModel;
using Tern.Domain.Documents;
using Tern.Domain.Merging;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Markdown;

namespace Tern.App.Documents;

/// <summary>A disagreement between the open buffer and the file on disk that the user has to resolve.</summary>
public sealed record ConflictState(ConflictKind Kind, DiskBaseline? Disk);

public enum DiskCheckResult
{
    Unchanged,
    BaselineRefreshed,
    ReloadedSilently,
    Conflict,
}

public sealed record TabSaveOptions(bool EnsureFinalNewline = false, bool TrimTrailingWhitespace = false);

/// <summary>
/// One open document: its <see cref="DocumentSession"/>, the structure derived from it, and the save / conflict /
/// reload workflows. UI-framework free so the whole workflow is testable with real files.
/// </summary>
public sealed partial class DocumentTab : ObservableObject, IDisposable
{
    private readonly DocumentFileService _files;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private CancellationTokenSource? _analysisCts;
    private string _mergeBase;
    private (DiskBaseline Baseline, string Text, long Revision)? _pendingMerge;
    private long _analysisGeneration;
    private bool _disposed;

    public string? RecoveredFromPath { get; set; }
    public bool IsRecovered { get; set; }
    /// <summary>Unsaved web imports must not inherit a local workspace's resource directory.</summary>
    private bool _isExternalDraft;
    public bool IsExternalDraft
    {
        get => _isExternalDraft;
        set
        {
            if (SetProperty(ref _isExternalDraft, value)) OnPropertyChanged(nameof(CanRename));
        }
    }
    public string? SourceUrl { get; set; }
    /// <summary>A local draft explicitly created outside all workspace roots.</summary>
    public bool IsStandaloneDraft { get; set; }
    public string? RecoveryName { get; set; }

    [ObservableProperty]
    private IReadOnlyList<string> _linkedImageFiles = [];

    [ObservableProperty]
    private long? _checkpointRevision;

    [ObservableProperty]
    private string? _recoveryError;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private ConflictState? _conflict;

    [ObservableProperty]
    private MarkdownAnalysis? _analysis;

    [ObservableProperty]
    private MarkdownProfile _profile;

    [ObservableProperty]
    private string? _readOnlyReason;

    [ObservableProperty]
    private string? _lastSaveError;

    [ObservableProperty]
    private bool _isInaccessible;

    internal void SetAccessFailure(bool inaccessible) => IsInaccessible = inaccessible;

    [ObservableProperty]
    private string _encodingText = "UTF-8";

    [ObservableProperty]
    private string _lineEndingText = "LF";

    public DocumentTab(DocumentSession session, DocumentFileService files, MarkdownProfile profile, string? readOnlyReason = null)
    {
        Session = session;
        _files = files;
        _profile = profile;
        _readOnlyReason = readOnlyReason;
        _mergeBase = session.GetSnapshot().Text;
        _displayName = session.DisplayName;
        session.StateChanged += OnSessionChanged;
        SyncFromSession();
        ScheduleAnalysis(TimeSpan.Zero);
    }

    public DocumentSession Session { get; }
    public bool CanRename => !IsExternalDraft || FilePath is not null;

    public string? FilePath => Session.FilePath;

    public bool IsUntitled => !Session.HasFile;

    public bool IsReadOnly => Session.IsReadOnly;

    /// <summary>Raised on the thread that created the tab each time a newer analysis is available.</summary>
    public event EventHandler? AnalysisChanged;

    public static async Task<DocumentTab> OpenAsync(string path, DocumentFileService files, MarkdownProfile profile, TextEncodingKind? forcedEncoding = null, CancellationToken cancellationToken = default)
    {
        var loaded = await files.LoadAsync(path, forcedEncoding, cancellationToken).ConfigureAwait(true);
        var readOnly = loaded.ShouldOpenReadOnly
            ? loaded.DecodeFailure == DecodeFailure.InvalidSequence
                ? "This file is not valid in its detected encoding. Choose an encoding to reopen it; it is read-only so nothing is overwritten."
                : "This file cannot be saved byte-for-byte in the detected encoding, so it is read-only to protect it."
            : null;
        var session = new DocumentSession(loaded.Path, loaded.Text, loaded.Format, loaded.Baseline, readOnly is not null);
        return new DocumentTab(session, files, profile, readOnly);
    }

    public static DocumentTab CreateUntitled(DocumentFileService files, MarkdownProfile profile, string text = "")
    {
        var format = LineEndingAnalyzer.Describe(TextEncodingKind.Utf8, false, text);
        if (text.AsSpan().IndexOfAny('\r', '\n') < 0)
            format = format with { DominantLineEnding = LineEnding.CrLf };
        var tab = new DocumentTab(new DocumentSession(null, string.Empty, format, null), files, profile);
        if (text.Length > 0) tab.ApplyEdits([TextEdit.Insert(0, text)]);
        return tab;
    }

    public string CurrentText => Session.Buffer.GetText();

    public void ApplyEdits(IReadOnlyList<TextEdit> edits)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Session.ApplyEdits(edits);
    }

    /// <summary>The text of the file as last loaded or saved; the common ancestor for three-way merges.</summary>
    public string MergeBase => _mergeBase;

    public void SetProfile(MarkdownProfile profile)
    {
        Profile = profile;
        ScheduleAnalysis(TimeSpan.Zero);
    }

    public async Task<SaveOutcome> SaveAsync(TabSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (Session.FilePath is null)
        {
            throw new InvalidOperationException("An untitled document must be saved with a path.");
        }

        return await SaveToAsync(Session.FilePath, Session.Baseline, overwriteExisting: false, options, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Writes the document to a new path and makes that path the document's file.</summary>
    public async Task<SaveOutcome> SaveAsAsync(string path, bool overwriteExisting, TabSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sameFile = Session.FilePath is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(Session.FilePath), StringComparison.OrdinalIgnoreCase);
        var expected = sameFile ? Session.Baseline : overwriteExisting ? await _files.ReadBaselineAsync(path, cancellationToken).ConfigureAwait(true) : null;
        return await SaveAsCheckedAsync(path, expected, options, cancellationToken).ConfigureAwait(true);
    }

    internal Task<SaveOutcome> SaveAsCheckedAsync(string path, DiskBaseline? expected, TabSaveOptions? options = null, CancellationToken cancellationToken = default) =>
        SaveToAsync(path, expected, overwriteExisting: false, options, cancellationToken, allowReadOnlyCopy: true);

    /// <summary>Writes a copy without changing this document's identity, baseline or dirty state.</summary>
    public async Task<SaveOutcome> SaveCopyAsync(string path, bool overwriteExisting, CancellationToken cancellationToken = default)
    {
        var expected = overwriteExisting ? await _files.ReadBaselineAsync(path, cancellationToken).ConfigureAwait(true) : null;
        return await SaveCopyCheckedAsync(path, expected, cancellationToken).ConfigureAwait(true);
    }

    internal async Task<SaveOutcome> SaveCopyCheckedAsync(string path, DiskBaseline? expected, CancellationToken cancellationToken = default)
    {
        if (_disposed) return new SaveFailed("The document is closed.");
        if (Session.FilePath is { } source && FileIdentity.AreSameFile(source, path))
            return new SaveFailed("Choose a separate copy filename. A copy cannot overwrite its source.");
        var text = CurrentText;
        List<string> resources;
        try { resources = await CopyResourcesAsync(path, text, cancellationToken).ConfigureAwait(true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new SaveFailed("Attachments could not be copied: " + ex.Message); }
        try
        {
            var outcome = await _files.SaveAsync(path, text, SavedFormat(text), expected, overwriteExisting: false, cancellationToken).ConfigureAwait(true);
            if (outcome is not SaveSucceeded) RemoveCreatedResources(resources);
            return outcome;
        }
        catch { RemoveCreatedResources(resources); throw; }
    }

    private async Task<SaveOutcome> SaveToAsync(string path, DiskBaseline? expected, bool overwriteExisting, TabSaveOptions? options, CancellationToken ct, bool allowReadOnlyCopy = false)
    {
        if (_disposed) return new SaveFailed("The document is closed.");
        var readOnlyCopy = Session.IsReadOnly && allowReadOnlyCopy;
        if (Session.IsReadOnly && !readOnlyCopy)
        {
            return new SaveFailed(ReadOnlyReason ?? "The document is read-only.");
        }

        if (readOnlyCopy && FilePath is { } original && FileIdentity.AreSameFile(original, path))
            return new SaveFailed("Choose a separate copy filename. The original bytes are protected.");

        ct.ThrowIfCancellationRequested();
        if (!readOnlyCopy) PrepareForSave(options);
        var snapshot = Session.GetSnapshot();
        var savedText = snapshot.Text;
        var savedFormat = readOnlyCopy ? SavedFormat(savedText) with { Encoding = TextEncodingKind.Utf8, HasBom = false } : SavedFormat(savedText);
        var savedSnapshot = new DocumentSnapshot(snapshot.Revision, savedText);
        List<string> resources;
        try { resources = await CopyResourcesAsync(path, savedText, ct).ConfigureAwait(true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LastSaveError = ex.Message; return new SaveFailed("Attachments could not be saved: " + ex.Message); }
        SaveOutcome outcome;
        try { outcome = await _files.SaveAsync(path, savedText, savedFormat, expected, overwriteExisting, ct).ConfigureAwait(true); }
        catch { RemoveCreatedResources(resources); throw; }
        if (outcome is not SaveSucceeded) RemoveCreatedResources(resources);
        switch (outcome)
        {
            case SaveSucceeded ok:
                ResourceDocumentPath = null;
                ResourceFiles = new Dictionary<string, byte[]>();
                IsInaccessible = false;
                Session.MarkSaved(savedSnapshot, savedFormat, ok.Baseline, path);
                if (readOnlyCopy) { Session.SetReadOnly(false); ReadOnlyReason = null; }
                _mergeBase = savedText;
                Conflict = null;
                LastSaveError = null;
                break;
            case SaveConflict conflict:
                // A conflict at a different Save As destination does not describe this backing file.
                if (Session.FilePath is { } currentPath && string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase))
                    Conflict = new ConflictState(conflict.Kind, conflict.Disk);
                break;
            case SaveFailed failed:
                LastSaveError = failed.Reason;
                break;
        }

        return outcome;
    }

    private TextFileFormat SavedFormat(string text) =>
        LineEndingAnalyzer.Describe(Session.Format.Encoding, Session.Format.HasBom, text)
            with { DominantLineEnding = Session.Format.DominantLineEnding };

    private void PrepareForSave(TabSaveOptions? options)
    {
        if (options is null)
        {
            return;
        }

        var edits = new List<TextEdit>();
        var text = Session.Buffer.GetText();
        if (options.TrimTrailingWhitespace)
        {
            edits.AddRange(WhitespaceEdits.TrimTrailing(text));
        }

        if (options.EnsureFinalNewline && text.Length > 0 && text[^1] is not ('\n' or '\r'))
        {
            edits.Add(TextEdit.Insert(text.Length, Session.Format.NewLineText));
        }

        // Trailing-space removal at the very end and the newline insertion can touch the same offset.
        if (edits.Count > 0)
        {
            Session.ApplyEdits(WhitespaceEdits.Normalize(edits, text.Length));
        }
    }

    /// <summary>Compares the file on disk with the baseline and decides whether anything needs the user's attention.</summary>
    public async Task<DiskCheckResult> CheckDiskAsync(bool autoReloadClean, CancellationToken cancellationToken = default)
    {
        if (Session.FilePath is not { } path || Session.Baseline is null)
        {
            return DiskCheckResult.Unchanged;
        }

        var revision = Session.Revision;
        var disk = await _files.ReadBaselineAsync(path, cancellationToken).ConfigureAwait(true);
        if (_disposed || Session.FilePath != path || !Session.IsCurrent(revision)) return DiskCheckResult.Unchanged;
        if (disk is null)
        {
            if (Conflict?.Kind == ConflictKind.DeletedOnDisk)
            {
                return DiskCheckResult.Conflict;
            }

            Conflict = new ConflictState(ConflictKind.DeletedOnDisk, null);
            return DiskCheckResult.Conflict;
        }

        if (disk.SameContentAs(Session.Baseline))
        {
            Conflict = null;

            return DiskCheckResult.Unchanged;
        }

        // Our own saves and tools that rewrite identical text both raise watcher events; compare real content first.
        var loaded = await _files.LoadAsync(path, null, cancellationToken).ConfigureAwait(true);
        if (_disposed || Session.FilePath != path || !Session.IsCurrent(revision)) return DiskCheckResult.Unchanged;
        if (!loaded.ShouldOpenReadOnly && loaded.Text == CurrentText)
        {
            // Matching characters do not satisfy an unsaved encoding/BOM or input-line-ending
            // preference. Accept the matching disk text as the new base, but retain that intent.
            if (Session.IsDirty && Session.Format != loaded.Format)
                Session.UpdateBaseline(loaded.Baseline);
            else
                Session.MarkSaved(Session.GetSnapshot(), loaded.Format, loaded.Baseline);
            Session.SetReadOnly(false);
            ReadOnlyReason = null;
            _mergeBase = loaded.Text;
            Conflict = null;
            return DiskCheckResult.BaselineRefreshed;
        }

        if (!Session.IsDirty && autoReloadClean)
        {
            Session.ReplaceWithDiskContent(loaded.Text, loaded.Format, loaded.Baseline);
            Session.SetReadOnly(loaded.ShouldOpenReadOnly);
            ReadOnlyReason = loaded.ShouldOpenReadOnly ? "The reloaded file cannot be saved byte-for-byte." : null;
            _mergeBase = loaded.Text;
            Conflict = null;
            return DiskCheckResult.ReloadedSilently;
        }

        Conflict = new ConflictState(ConflictKind.ModifiedOnDisk, loaded.Baseline);
        return DiskCheckResult.Conflict;
    }

    /// <summary>Discards the open buffer in favour of whatever is on disk now.</summary>
    public async Task<bool> TakeDiskVersionAsync(CancellationToken cancellationToken = default)
    {
        var path = Session.FilePath ?? throw new InvalidOperationException("The document has no file.");
        var revision = Session.Revision;
        var loaded = await _files.LoadAsync(path, null, cancellationToken).ConfigureAwait(true);
        if (_disposed || Session.FilePath != path || !Session.IsCurrent(revision)) return false;
        Session.ReplaceWithDiskContent(loaded.Text, loaded.Format, loaded.Baseline);
        Session.SetReadOnly(loaded.ShouldOpenReadOnly);
        ReadOnlyReason = loaded.ShouldOpenReadOnly ? "The reloaded file cannot be saved byte-for-byte." : null;
        _mergeBase = loaded.Text;
        Conflict = null;
        return true;
    }

    /// <summary>Overwrites the file on disk with the open buffer, accepting that newer disk content is lost.</summary>
    public async Task<SaveOutcome> KeepMineAndOverwriteAsync(TabSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        var path = Session.FilePath ?? throw new InvalidOperationException("The document has no file.");
        var disk = await _files.ReadBaselineAsync(path, cancellationToken).ConfigureAwait(true);
        return await SaveToAsync(path, disk, overwriteExisting: true, options, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>The user chose to continue editing a document whose file vanished; the next save recreates it.</summary>
    public void AcceptMissingFile()
    {
        Session.UpdateBaseline(null);
        Conflict = null;
    }

    /// <summary>Computes the three-way merge of the saved base, the open buffer and the current disk content.</summary>
    public async Task<MergeResult> BuildMergeAsync(CancellationToken cancellationToken = default)
    {
        var path = Session.FilePath ?? throw new InvalidOperationException("The document has no file.");
        var disk = await _files.LoadAsync(path, null, cancellationToken).ConfigureAwait(true);
        _pendingMerge = (disk.Baseline, disk.Text, Session.Revision);
        return ThreeWayMerger.Merge(_mergeBase, CurrentText, disk.Text);
    }

    /// <summary>Puts the user-approved merged text into the buffer, anchored to the current disk version so a save succeeds.</summary>
    public async Task<bool> ApplyMergedTextAsync(string mergedText, CancellationToken cancellationToken = default)
    {
        var path = Session.FilePath ?? throw new InvalidOperationException("The document has no file.");
        var disk = await _files.LoadAsync(path, null, cancellationToken).ConfigureAwait(true);
        var pending = _pendingMerge;
        _pendingMerge = null;
        if (pending is null || !Session.IsCurrent(pending.Value.Revision) || !disk.Baseline.SameContentAs(pending.Value.Baseline))
        {
            Conflict = new ConflictState(ConflictKind.ModifiedOnDisk, disk.Baseline);
            return false;
        }
        var current = CurrentText;
        if (current != mergedText)
        {
            Session.ApplyEdits([new TextEdit(0, current.Length, mergedText)]);
        }

        Session.UpdateBaseline(pending.Value.Baseline);
        _mergeBase = pending.Value.Text;
        Conflict = null;
        return true;
    }

    /// <summary>Re-reads the file using an explicitly chosen encoding; refused when there are unsaved edits.</summary>
    public async Task<bool> ReopenWithEncodingAsync(TextEncodingKind encoding, CancellationToken cancellationToken = default)
    {
        var path = Session.FilePath ?? throw new InvalidOperationException("The document has no file.");
        if (Session.IsDirty)
        {
            throw new InvalidOperationException("Save or discard changes before reopening with another encoding.");
        }

        var revision = Session.Revision;
        var loaded = await _files.LoadAsync(path, encoding, cancellationToken).ConfigureAwait(true);
        if (_disposed || Session.FilePath != path || !Session.IsCurrent(revision)) return false;
        if (loaded.ShouldOpenReadOnly)
        {
            LastSaveError = "The file cannot be decoded exactly with the selected encoding. The current document was kept unchanged.";
            return false;
        }
        Session.ReplaceWithDiskContent(loaded.Text, loaded.Format, loaded.Baseline);
        Session.SetReadOnly(loaded.ShouldOpenReadOnly);
        ReadOnlyReason = loaded.ShouldOpenReadOnly ? "The file is not valid in the chosen encoding." : null;
        _mergeBase = loaded.Text;
        LastSaveError = null;
        return true;
    }

    /// <summary>Changes the encoding the file will be saved in; false when some characters cannot be represented.</summary>
    public bool TryConvertEncoding(TextEncodingKind encoding, bool withBom)
    {
        if (!TextFileCodec.CanRepresent(CurrentText, encoding))
        {
            return false;
        }

        var bomAllowed = TextEncodingIds.SupportsBom(encoding);
        Session.ChangeFormat(Session.Format with { Encoding = encoding, HasBom = withBom && bomAllowed });
        return true;
    }

    /// <summary>Rewrites every line break to the chosen convention and records it as the document's format.</summary>
    public void ConvertLineEndings(LineEnding target)
    {
        var text = CurrentText;
        var edits = new List<TextEdit>();
        var replacement = target switch { LineEnding.CrLf => "\r\n", LineEnding.Cr => "\r", _ => "\n" };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                var length = i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                if (!string.Equals(text.Substring(i, length), replacement, StringComparison.Ordinal))
                {
                    edits.Add(new TextEdit(i, length, replacement));
                }

                i += length - 1;
            }
            else if (text[i] == '\n' && !string.Equals(replacement, "\n", StringComparison.Ordinal))
            {
                edits.Add(new TextEdit(i, 1, replacement));
            }
        }

        if (edits.Count > 0)
        {
            Session.ApplyEdits(edits);
        }

        Session.ChangeFormat(Session.Format with { DominantLineEnding = target, HasMixedLineEndings = false });
        SyncFromSession();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _analysisGeneration++;
        Session.StateChanged -= OnSessionChanged;
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        SyncFromSession();
        ScheduleAnalysis(TimeSpan.FromMilliseconds(180));
    }

    private void SyncFromSession()
    {
        IsDirty = Session.IsDirty;
        DisplayName = IsRecovered && !Session.HasFile ? "Recovered — " + (RecoveryName ?? "Untitled") : Session.DisplayName;
        EncodingText = Session.Format.EncodingDisplayName;
        LineEndingText = Session.Format.LineEndingDisplayName;
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(IsUntitled));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanRename));
    }

    private void ScheduleAnalysis(TimeSpan delay)
    {
        if (_disposed) return;
        var generation = ++_analysisGeneration;
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        var cts = _analysisCts = new CancellationTokenSource();
        var token = cts.Token;
        var snapshot = Session.GetSnapshot();
        var profile = Profile;
        if (snapshot.Text.Length == 0)
        {
            Publish(new MarkdownAnalysis { Profile = profile, Lines = new LineIndex(string.Empty) }, snapshot.Revision, generation);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }

                var result = MarkdownAnalyzer.Analyze(snapshot.Text, profile);
                token.ThrowIfCancellationRequested();
                Publish(result, snapshot.Revision, generation);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void Publish(MarkdownAnalysis result, long revision, long generation)
    {
        void Apply()
        {
            if (_disposed || generation != _analysisGeneration || !Session.IsCurrent(revision))
            {
                return;
            }

            Analysis = result;
            AnalysisChanged?.Invoke(this, EventArgs.Empty);
        }

        if (_context is null)
        {
            Apply();
        }
        else
        {
            _context.Post(_ => Apply(), null);
        }
    }
}

internal static class WhitespaceEdits
{
    public static IEnumerable<TextEdit> TrimTrailing(string text)
    {
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var end = lineStart;
            while (end < text.Length && text[end] is not ('\r' or '\n'))
            {
                end++;
            }

            var trimmed = end;
            while (trimmed > lineStart && text[trimmed - 1] is ' ' or '\t')
            {
                trimmed--;
            }

            // Two trailing spaces are a Markdown hard line break, so a single deliberate pair is kept.
            var removable = end - trimmed;
            if (removable > 0 && !(removable == 2 && text[trimmed] == ' ' && end < text.Length))
            {
                yield return TextEdit.Delete(trimmed, removable);
            }

            if (end >= text.Length)
            {
                yield break;
            }

            lineStart = end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
        }
    }

    public static IReadOnlyList<TextEdit> Normalize(List<TextEdit> edits, int length)
    {
        var ordered = edits.OrderBy(e => e.Start).ThenBy(e => e.Length).ToList();
        var result = new List<TextEdit>();
        var cursor = 0;
        foreach (var edit in ordered)
        {
            if (edit.Start < cursor || edit.End > length)
            {
                continue;
            }

            result.Add(edit);
            cursor = edit.End;
        }

        return result;
    }
}
