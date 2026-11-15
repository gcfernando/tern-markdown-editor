using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tern.PreviewView;

/// <summary>A full-sized task glyph independent of Fluent's minimum checkbox dimensions and disabled colors.</summary>
internal sealed class TaskMarker : Control
{
    private readonly CheckBox _owner;
    private readonly double _fontSize;
    public TaskMarker(CheckBox owner, double fontSize)
    {
        _owner = owner; _fontSize = fontSize;
        owner.PropertyChanged += (_, e) =>
        {
            if (e.Property == CheckBox.IsCheckedProperty || e.Property == IsKeyboardFocusWithinProperty) InvalidateVisual();
        };
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Math.Clamp(_fontSize * .88, 12, 32);
        var rect = new Rect(3, Math.Max(3, (_fontSize * 1.5 - size) / 2), size, size);
        var color = PreviewTheme.Resolve(this, PreviewKeys.Muted);
        var accent = PreviewTheme.Resolve(this, PreviewKeys.Accent);
        context.DrawRectangle(null, new Pen(color, 1.4), rect, 2, 2);
        if (_owner.IsChecked == true)
        {
            var pen = new Pen(accent, 2);
            context.DrawLine(pen, new Point(rect.X + size * .2, rect.Y + size * .52), new Point(rect.X + size * .43, rect.Y + size * .76));
            context.DrawLine(pen, new Point(rect.X + size * .43, rect.Y + size * .76), new Point(rect.X + size * .83, rect.Y + size * .22));
        }
        if (_owner.IsKeyboardFocusWithin) context.DrawRectangle(null, new Pen(accent, 2), rect.Inflate(2), 3, 3);
    }
}
