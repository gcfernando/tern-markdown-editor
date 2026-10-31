using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Tern.Desktop.Views;

/// <summary>One selectable row in a <see cref="QuickPicker"/>.</summary>
public sealed record PickerItem(string Title, string Detail, string Hint, object? Payload, Action? RemoveReference = null);

/// <summary>
/// The floating search-and-pick box used by the command palette, quick open, go to line and workspace search.
/// The owner supplies the rows for the current text; Enter accepts the highlighted row, Escape dismisses.
/// </summary>
public sealed class QuickPicker : Border
{
    private readonly TextBox _input = new() { Padding = new Thickness(12, 10) };
    private readonly ListBox _list = new() { MaxHeight = 380 };
    private readonly TextBlock _empty = new() { Margin = new Thickness(14, 10), IsVisible = false };
    private Func<string, IReadOnlyList<PickerItem>> _provider = _ => [];
    private Action<PickerItem>? _accept;
    private Action? _dismiss;
    private string _emptyText = "No matches";
    private Func<string, string>? _emptyTextProvider;

    public QuickPicker()
    {
        Width = 640;
        VerticalAlignment = VerticalAlignment.Top;
        HorizontalAlignment = HorizontalAlignment.Center;
        Margin = new Thickness(0, 70, 0, 0);
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        this[!BackgroundProperty] = this.GetResourceObservable("Tern.Panel").ToBinding();
        this[!BorderBrushProperty] = this.GetResourceObservable("Tern.Border").ToBinding();
        _empty.Classes.Add("muted");
        _input.Classes.Add("onPanel");
        _input[!TextBox.FontSizeProperty] = this.GetResourceObservable("Tern.UiFontSize").ToBinding();
        _input.Background = null;
        _input[!BackgroundProperty] = this.GetResourceObservable("Tern.Panel").ToBinding();
        AutomationProperties.SetName(this, "Quick picker");
        AutomationProperties.SetName(_input, "Search");
        AutomationProperties.SetName(_list, "Results");
        AutomationProperties.SetName(_empty, "Search result status");
        _empty.TextWrapping = TextWrapping.Wrap;

        _list.ItemTemplate = new FuncDataTemplate<PickerItem>((item, _) =>
        {
            if (item is null)
            {
                return new Grid();
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8, MinHeight = 44 };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = item.Title, TextTrimming = TextTrimming.CharacterEllipsis });
            if (item.Detail.Length > 0)
            {
                var detail = new TextBlock { Text = item.Detail, TextTrimming = TextTrimming.CharacterEllipsis };
                detail[!TextBlock.FontSizeProperty] = this.GetResourceObservable("Tern.SmallFontSize").ToBinding();
                detail.Classes.Add("muted");
                text.Children.Add(detail);
            }

            if (item.RemoveReference is { } removeReference)
            {
                var icon = new Avalonia.Controls.Shapes.Path
                {
                    Width = 12, Height = 12, StrokeThickness = 1.5,
                    Data = Geometry.Parse("M1,1 L9,9 M1,9 L9,1"),
                };
                icon[!Avalonia.Controls.Shapes.Shape.StrokeProperty] = this.GetResourceObservable("Tern.Danger").ToBinding();
                var remove = new Button { Content = icon, Width = 36, MinHeight = 36, Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
                remove.Classes.Add("flat");
                AutomationProperties.SetName(remove, "Remove file reference");
                ToolTip.SetTip(remove, "Remove this reference from Library and Recent. The file and open edits are kept.");
                remove.Click += (_, e) =>
                {
                    e.Handled = true;
                    removeReference();
                    RefreshResults();
                    _input.Focus();
                };
                grid.Children.Add(remove);
                Grid.SetColumn(remove, 2);
            }

            grid.Children.Add(text);
            if (item.Hint.Length > 0)
            {
                var hint = new TextBlock { Text = item.Hint, VerticalAlignment = VerticalAlignment.Center };
                hint.Classes.Add("muted");
                grid.Children.Add(hint);
                Grid.SetColumn(hint, 1);
            }

            return grid;
        });

        _input.TextChanged += (_, _) => Refresh();
        _input.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _list.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control source && (source is Button || source.GetVisualAncestors().OfType<Button>().Any()))
            { e.Handled = true; return; }
            AcceptSelected();
        };

        var rule = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 4) };
        rule[!BackgroundProperty] = this.GetResourceObservable("Tern.Border").ToBinding();
        Child = new StackPanel { Children = { _input, rule, _list, _empty } };
    }

    internal TextBox Input => _input;

    internal ListBox Results => _list;

    /// <summary>Refresh current rows without accepting one or dismissing the picker.</summary>
    public void RefreshResults() => Refresh();

    public void Open(string watermark, string initialText, Func<string, IReadOnlyList<PickerItem>> provider, Action<PickerItem> accept, Action dismiss, string emptyText = "No matches", Func<string, string>? emptyTextProvider = null)
    {
        _provider = provider;
        _accept = accept;
        _dismiss = dismiss;
        _emptyText = emptyText;
        _emptyTextProvider = emptyTextProvider;
        _input.PlaceholderText = watermark;
        _input.Text = initialText;
        _input.CaretIndex = initialText.Length;
        Refresh();
        _input.Focus();
    }

    private void Refresh()
    {
        var query = _input.Text ?? string.Empty;
        var items = _provider(query);
        _empty.Text = _emptyTextProvider?.Invoke(query) ?? _emptyText;
        _list.ItemsSource = items;
        _list.SelectedIndex = items.Count > 0 ? 0 : -1;
        _empty.IsVisible = items.Count == 0;
        _list.IsVisible = items.Count > 0;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Step(1);
                e.Handled = true;
                break;
            case Key.Up:
                Step(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                AcceptSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                _dismiss?.Invoke();
                e.Handled = true;
                break;
        }
    }

    private void Step(int delta)
    {
        var count = _list.ItemCount;
        if (count == 0)
        {
            return;
        }

        _list.SelectedIndex = (_list.SelectedIndex + delta + count) % count;
        _list.ScrollIntoView(_list.SelectedIndex);
    }

    private void AcceptSelected()
    {
        if (_list.SelectedItem is PickerItem item)
        {
            _accept?.Invoke(item);
        }
    }
}
