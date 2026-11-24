using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Tern.Desktop.Views;

internal sealed class DialogActions : Grid
{
    private readonly Button[] _buttons;
    private readonly Button _cancel;
    private readonly WrapPanel _secondary = GuideParts.ButtonRow();
    private readonly WrapPanel _main = GuideParts.ButtonRow();

    public Button Primary { get; private set; }

    public DialogActions(Button primary, Button cancel, params Button[] secondary)
    {
        _buttons = [.. secondary.Concat([primary, cancel]).Distinct()];
        _cancel = cancel;
        Primary = primary;
        ColumnDefinitions = new("*,Auto");
        RowDefinitions = new("Auto,Auto");
        this[!ColumnSpacingProperty] = GuideParts.Space(8).ToBinding();
        _secondary.HorizontalAlignment = HorizontalAlignment.Left;
        Children.Add(_secondary);
        Children.Add(_main);
        SetPrimary(primary);
    }

    public void SetPrimary(Button primary)
    {
        if (!_buttons.Contains(primary)) throw new ArgumentException("The primary action must belong to this footer.", nameof(primary));
        var selected = primary.Classes.Contains("danger") ? _cancel : primary;
        if (ReferenceEquals(Primary, selected) && _main.Children.Count > 0) return;
        Primary = selected;
        _secondary.Children.Clear();
        _main.Children.Clear();
        foreach (var button in _buttons)
        {
            button.MinHeight = 34;
            button.MinWidth = 88;
            button.IsDefault = ReferenceEquals(button, Primary) && !button.Classes.Contains("danger");
            button.IsCancel = ReferenceEquals(button, _cancel);
            button.Classes.Set("accent", button.IsDefault);
            if (!ReferenceEquals(button, Primary) && !ReferenceEquals(button, _cancel))
                _secondary.Children.Add(button);
        }
        _main.Children.Add(Primary);
        if (!ReferenceEquals(Primary, _cancel)) _main.Children.Add(_cancel);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _secondary.Measure(Size.Infinity);
        _main.Measure(Size.Infinity);
        // Stacking also lets a wide main row wrap. In its Auto column it would run past the window edge.
        var secondary = _secondary.DesiredSize.Width > 0 ? _secondary.DesiredSize.Width + ColumnSpacing : 0;
        var stacked = secondary + _main.DesiredSize.Width > availableSize.Width;
        RowSpacing = stacked ? GuideParts.SpaceValue(8) : 0;
        SetColumnSpan(_secondary, stacked ? 2 : 1);
        SetColumn(_main, stacked ? 0 : 1);
        SetRow(_main, stacked ? 1 : 0);
        SetColumnSpan(_main, stacked ? 2 : 1);
        return base.MeasureOverride(availableSize);
    }
}
