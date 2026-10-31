using Tern.Markdown;
using Tern.Workspaces;

namespace Tern.Search;

/// <summary>
/// Unsaved editor buffers, held in memory only. They are merged into search results at query time and never written
/// to the on-disk index. Thread-safe.
/// </summary>
public sealed class OverlayStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, OverlayDocument> _documents = new(WorkspacePaths.Comparer);
    private readonly string _root;
    private readonly MarkdownProfile _profile;

    public OverlayStore(string root, MarkdownProfile profile = MarkdownProfile.Extended)
    {
        _root = WorkspacePaths.Normalize(root);
        _profile = profile;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _documents.Count;
            }
        }
    }

    /// <summary>Replaces the buffer for <paramref name="path"/>. Cheap: the text is analysed lazily, on the first query that needs it.</summary>
    public void Set(string path, string text)
    {
        var full = WorkspacePaths.Normalize(path);
        if (!WorkspacePaths.IsUnder(_root, full))
        {
            return;
        }

        var relative = WorkspacePaths.GetRelative(_root, full);
        var profile = _profile;
        lock (_gate)
        {
            _documents[relative] = new OverlayDocument(relative, new Lazy<ExtractedDocument>(() => MarkdownTextExtractor.Extract(relative, text, profile)));
        }
    }

    public void Clear(string path)
    {
        var full = WorkspacePaths.Normalize(path);
        if (!WorkspacePaths.IsUnder(_root, full))
        {
            return;
        }

        lock (_gate)
        {
            _documents.Remove(WorkspacePaths.GetRelative(_root, full));
        }
    }

    public void ClearAll()
    {
        lock (_gate)
        {
            _documents.Clear();
        }
    }

    public bool Contains(string path)
    {
        var full = WorkspacePaths.Normalize(path);
        lock (_gate)
        {
            return WorkspacePaths.IsUnder(_root, full) && _documents.ContainsKey(WorkspacePaths.GetRelative(_root, full));
        }
    }

    internal IReadOnlyList<OverlayDocument> Snapshot()
    {
        lock (_gate)
        {
            return [.. _documents.Values];
        }
    }
}

internal sealed class OverlayDocument
{
    private readonly Lazy<ExtractedDocument> _content;
    private readonly Lazy<FieldWords> _words;

    public OverlayDocument(string relativePath, Lazy<ExtractedDocument> content)
    {
        RelativePath = relativePath;
        _content = content;
        _words = new Lazy<FieldWords>(() => new FieldWords(
            Tokenizer.Words(content.Value.Title),
            Tokenizer.Words(string.Join('\n', content.Value.Headings)),
            Tokenizer.Words(string.Join(' ', content.Value.Tags)),
            Tokenizer.Words(relativePath),
            Tokenizer.Words(content.Value.Body)));
    }

    public string RelativePath { get; }

    public ExtractedDocument Content => _content.Value;

    public FieldWords Words => _words.Value;
}

internal sealed record FieldWords(List<string> Title, List<string> Headings, List<string> Tags, List<string> Path, List<string> Body);
