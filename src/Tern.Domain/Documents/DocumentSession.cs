using Tern.Domain.Text;

namespace Tern.Domain.Documents;

/// <summary>
/// Tracks revisions, dirty state and the disk baseline for one open document.
/// Viewing or switching modes never touches the session; only real edits advance <see cref="Revision"/>.
/// Not thread-safe: use from the UI thread and pass <see cref="DocumentSnapshot"/> values to background work.
/// </summary>
public sealed class DocumentSession
{
    private IDocumentBuffer _buffer;
    private DocumentSnapshot? _cachedSnapshot;
    private string _savedHash;
    private int _savedLength;
    private string? _currentHash;
    private TextFileFormat _savedFormat;

    public DocumentSession(string? filePath, string text, TextFileFormat format, DiskBaseline? baseline, bool isReadOnly = false)
    {
        FilePath = filePath;
        Format = format;
        Baseline = baseline;
        IsReadOnly = isReadOnly;
        _savedHash = ContentHasher.Hash(text);
        _savedLength = text.Length;
        _savedFormat = format;
        _buffer = new StringDocumentBuffer(text);
        _buffer.Changed += OnBufferChanged;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string? FilePath { get; private set; }

    public TextFileFormat Format { get; private set; }

    public DiskBaseline? Baseline { get; private set; }

    public bool IsReadOnly { get; private set; }

    public long Revision { get; private set; }

    public long SavedRevision { get; private set; }

    public bool IsDirty => Format != _savedFormat || _buffer.Length != _savedLength
        || (_currentHash ??= GetSnapshot().ContentHash) != _savedHash;

    /// <summary>True when the file has a path on disk; untitled documents must go through Save As.</summary>
    public bool HasFile => FilePath is not null;

    public string DisplayName => FilePath is null ? _draftName : Path.GetFileName(FilePath);
    public bool HasExplicitDraftName { get; private set; }
    private string _draftName = "New document";
    public void SetDraftName(string name)
    {
        if (HasFile) throw new InvalidOperationException("A saved document must be renamed on disk.");
        _draftName = name;
        HasExplicitDraftName = true;
        RaiseStateChanged();
    }
    public void UpdateFilePath(string path)
    {
        if (!HasFile) throw new InvalidOperationException("Use Save As for a new document.");
        FilePath = Path.GetFullPath(path);
        RaiseStateChanged();
    }

    public event EventHandler? StateChanged;

    public IDocumentBuffer Buffer => _buffer;

    /// <summary>Swaps the in-memory buffer for the editor's buffer, carrying the current text across.</summary>
    public void AttachBuffer(IDocumentBuffer buffer)
    {
        _buffer.Changed -= OnBufferChanged;
        _buffer = buffer;
        _buffer.Changed += OnBufferChanged;
        _cachedSnapshot = null;
        _currentHash = null;
    }

    public bool IsCurrent(long revision) => revision == Revision;

    public DocumentSnapshot GetSnapshot()
    {
        if (_cachedSnapshot is { } cached && cached.Revision == Revision)
        {
            return cached;
        }

        _cachedSnapshot = new DocumentSnapshot(Revision, _buffer.GetText());
        return _cachedSnapshot;
    }

    public void ApplyEdits(IReadOnlyList<TextEdit> edits)
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The document is read-only.");
        }

        _buffer.Apply(edits);
    }

    /// <summary>
    /// Records a successful save of <paramref name="saved"/>. Edits made after that snapshot keep the
    /// document dirty, so the newer recovery data must be retained.
    /// </summary>
    public void MarkSaved(DocumentSnapshot saved, TextFileFormat format, DiskBaseline baseline, string? newPath = null)
    {
        SavedRevision = saved.Revision;
        _savedHash = saved.ContentHash;
        _savedLength = saved.Text.Length;
        _savedFormat = format;
        // A later format conversion belongs to the newer revision, just like later text edits.
        if (Revision == saved.Revision)
        {
            Format = format;
        }
        Baseline = baseline;
        if (newPath is not null)
        {
            FilePath = newPath;
        }

        RaiseStateChanged();
    }

    /// <summary>Records that disk content changed without altering the buffer (for example a user keeps their copy).</summary>
    public void UpdateBaseline(DiskBaseline? baseline)
    {
        Baseline = baseline;
        RaiseStateChanged();
    }

    /// <summary>
    /// Changes how the document will be written (encoding, BOM, line-ending convention). The text on disk
    /// would differ after the next save, so this counts as a modification.
    /// </summary>
    public void ChangeFormat(TextFileFormat format)
    {
        if (format == Format)
        {
            return;
        }

        Format = format;
        Revision++;
        _cachedSnapshot = null;
        _currentHash = null;
        RaiseStateChanged();
    }

    public void SetReadOnly(bool readOnly)
    {
        IsReadOnly = readOnly;
        RaiseStateChanged();
    }

    /// <summary>Replaces the whole buffer with freshly loaded disk content and marks it clean.</summary>
    public void ReplaceWithDiskContent(string text, TextFileFormat format, DiskBaseline baseline)
    {
        Format = format;
        Baseline = baseline;
        _buffer.Apply([new TextEdit(0, _buffer.GetText().Length, text)]);
        SavedRevision = Revision;
        _savedHash = ContentHasher.Hash(text);
        _savedLength = text.Length;
        _savedFormat = format;
        RaiseStateChanged();
    }

    private void OnBufferChanged(object? sender, BufferChangedEventArgs e)
    {
        Revision++;
        _cachedSnapshot = null;
        _currentHash = null;

        // Undoing back to the saved text must clear the dirty flag; comparing hashes is only
        // needed on that path so ordinary typing stays O(1).
        if (e.IsUndoRedo && Format == _savedFormat && GetSnapshot().ContentHash == _savedHash)
        {
            SavedRevision = Revision;
        }

        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
