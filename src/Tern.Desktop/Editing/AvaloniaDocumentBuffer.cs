using AvaloniaEdit.Document;
using Tern.Domain.Documents;
using Tern.Domain.Text;

namespace Tern.Desktop.Editing;

/// <summary>
/// Exposes an AvaloniaEdit <see cref="TextDocument"/> as the session's <see cref="IDocumentBuffer"/>.
/// The editor's document is the single copy of the text; undo and redo are routed through here so
/// the session can tell them apart from typing and clear its dirty flag when the text returns to saved.
/// </summary>
public sealed class AvaloniaDocumentBuffer : IDocumentBuffer
{
    private bool _undoRedoInProgress;

    public AvaloniaDocumentBuffer(string text)
    {
        Document = new TextDocument(text);
        Document.UndoStack.ClearAll();
        Document.TextChanged += OnTextChanged;
    }

    public event EventHandler<BufferChangedEventArgs>? Changed;

    public TextDocument Document { get; }

    public string GetText() => Document.Text;

    public int Length => Document.TextLength;

    public void Apply(IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0)
        {
            return;
        }

        // Edits are expressed against the original text, so applying from the end keeps earlier offsets valid.
        var ordered = edits.OrderByDescending(e => e.Start).ToList();
        Document.UndoStack.StartUndoGroup();
        Document.BeginUpdate();
        try
        {
            foreach (var edit in ordered)
            {
                Document.Replace(edit.Start, edit.Length, edit.NewText);
            }
        }
        finally
        {
            Document.EndUpdate();
            Document.UndoStack.EndUndoGroup();
        }
    }

    public bool Undo() => RunHistory(Document.UndoStack.CanUndo, Document.UndoStack.Undo);

    public bool Redo() => RunHistory(Document.UndoStack.CanRedo, Document.UndoStack.Redo);

    private bool RunHistory(bool possible, Action step)
    {
        if (!possible)
        {
            return false;
        }

        _undoRedoInProgress = true;
        try
        {
            step();
        }
        finally
        {
            _undoRedoInProgress = false;
        }

        return true;
    }

    private void OnTextChanged(object? sender, EventArgs e) =>
        Changed?.Invoke(this, new BufferChangedEventArgs(_undoRedoInProgress));
}
