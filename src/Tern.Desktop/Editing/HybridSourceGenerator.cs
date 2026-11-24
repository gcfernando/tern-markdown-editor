using Avalonia.Controls;
using AvaloniaEdit.Rendering;

namespace Tern.Desktop.Editing;

internal sealed record SourceDecoration(int Offset, int Length, int Line, Func<Control> Create)
{
    private Control? _element;
    public Control Element => _element ??= Create();
}

internal sealed class HybridSourceGenerator : VisualLineElementGenerator
{
    private SourceDecoration[] _items = [];
    private Func<bool> _isCurrent = () => false;
    public int CaretLine { get; set; }

    public void SetContent(IEnumerable<SourceDecoration> items, Func<bool> isCurrent)
    {
        _items = items.OrderBy(item => item.Offset).ToArray();
        _isCurrent = isCurrent;
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (!_isCurrent()) return -1;
        var low = 0;
        var high = _items.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_items[middle].Offset < startOffset) low = middle + 1; else high = middle;
        }
        for (var i = low; i < _items.Length; i++)
        {
            var item = _items[i];
            if (item.Offset > CurrentContext.VisualLine.LastDocumentLine.EndOffset) break;
            if (item.Line != CaretLine) return item.Offset;
        }
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (!_isCurrent()) return null;
        var item = _items.FirstOrDefault(item => item.Offset == offset);
        return item is null ? null : new InlineObjectElement(item.Length, item.Element);
    }
}
