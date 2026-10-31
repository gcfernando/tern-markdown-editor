using Tern.Domain.Text;

namespace Tern.Domain.Documents;

public sealed class BufferChangedEventArgs(bool isUndoRedo) : EventArgs
{
    public bool IsUndoRedo { get; } = isUndoRedo;
}

/// <summary>
/// The live, canonical text of one document. The editor control owns the real buffer;
/// the session only observes it, so there is exactly one copy of the text being edited.
/// </summary>
public interface IDocumentBuffer
{
    event EventHandler<BufferChangedEventArgs>? Changed;

    string GetText();

    /// <summary>Allows editor buffers to report length without materializing their full text.</summary>
    int Length => GetText().Length;

    /// <summary>Applies edits (all expressed against the current text) as one undoable transaction.</summary>
    void Apply(IReadOnlyList<TextEdit> edits);
}

/// <summary>Plain in-memory buffer used when no editor control is attached (tests, headless services).</summary>
public sealed class StringDocumentBuffer(string text) : IDocumentBuffer
{
    private string _text = text;

    public event EventHandler<BufferChangedEventArgs>? Changed;

    public string GetText() => _text;

    public void Apply(IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0)
        {
            return;
        }

        _text = TextEdit.ApplyAll(_text, edits);
        Changed?.Invoke(this, new BufferChangedEventArgs(false));
    }
}
