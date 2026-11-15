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
public sealed record PickerItem(string Title, string Detail, string Hint, object? Payload, Action? RemoveReference = null,
    IReadOnlyList<Tern.Search.HighlightRange>? TitleRanges = null, IReadOnlyList<Tern.Search.HighlightRange>? DetailRanges = null,
    Func<Task>? CloseDocument = null);
public enum PickerPresentationState { EmptyQuery, Searching, Results, NoResults, Error }

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
    private Func<string, CancellationToken, Task<IReadOnlyList<PickerItem>>>? _asyncProvider;
    private CancellationTokenSource? _queryCancellation;
    private long _queryGeneration;
    private string? _lastQuery;
    private Func<bool>? _isCurrent;
    private Func<string?>? _errorProvider;
    private readonly TextBlock _scope = new() { Margin = new Thickness(14, 8), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _count = new() { Margin = new Thickness(14, 4), IsVisible = false };
    private readonly Button _recovery = new() { Margin = new Thickness(14, 8), IsVisible = false };
    private Func<Task>? _recover;
    internal PickerPresentationState State { get; private set; }

    public QuickPicker()
    {
        MaxWidth = 640;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(12, 32, 12, 12);
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
        AutomationProperties.SetName(_scope, "Search scope");
        AutomationProperties.SetName(_count, "Completed result count");
        AutomationProperties.SetLiveSetting(_empty, AutomationLiveSetting.Polite);
        _empty.TextWrapping = TextWrapping.Wrap;

        _list.ItemTemplate = new FuncDataTemplate<PickerItem>((item, _) =>
        {
            if (item is null)
            {
                return new Grid();
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8, MinHeight = 44 };
            var text = new StackPanel();
            text.Children.Add(Highlighted(item.Title, item.TitleRanges));
            if (item.Detail.Length > 0)
            {
                var detail = Highlighted(item.Detail, item.DetailRanges);
                detail[!TextBlock.FontSizeProperty] = this.GetResourceObservable("Tern.SmallFontSize").ToBinding();
                detail.Classes.Add("muted");
                text.Children.Add(detail);
            }

            if (item.RemoveReference is { } removeReference)
            {
                var remove = new Button { Content = "⋯", Width = 36, MinHeight = 36, Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
                remove.Classes.Add("flat");
                AutomationProperties.SetName(remove, "Recent entry actions");
                ToolTip.SetTip(remove, "Recent entry actions; the file and open edits are kept.");
                var forget = new MenuItem { Header = "Forget recent entry" };
                AutomationProperties.SetName(forget, "Forget recent entry");
                remove.Flyout = new MenuFlyout { Items = { forget } };
                forget.Click += (_, e) =>
                {
                    e.Handled = true;
                    removeReference();
                    RefreshResults();
                    _input.Focus();
                };
                grid.Children.Add(remove);
                Grid.SetColumn(remove, 2);
            }
            if (item.CloseDocument is { } closeDocument)
            {
                var close = new Button { Content = "×", Width = 36, MinHeight = 36, Classes = { "flat" } };
                AutomationProperties.SetName(close, "Close document " + item.Title);
                close.Click += async (_, e) => { e.Handled = true; await closeDocument(); RefreshResults(); _input.Focus(); };
                grid.Children.Add(close); Grid.SetColumn(close, 2);
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
        DetachedFromVisualTree += (_, _) => { _queryGeneration++; _queryCancellation?.Cancel(); };
        AddHandler(KeyDownEvent, OnPickerKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _list.Tapped += (_, e) =>
        {
            if (e.Source is Control source && (source is Button || source.GetVisualAncestors().OfType<Button>().Any()))
            { e.Handled = true; return; }
            if (e.Source is Control row && (row is ListBoxItem || row.GetVisualAncestors().OfType<ListBoxItem>().Any()))
                AcceptSelected();
        };

        var rule = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 4) };
        rule[!BackgroundProperty] = this.GetResourceObservable("Tern.Border").ToBinding();
        _recovery.Click += async (_, _) =>
        {
            if (_recover is not { } action || _isCurrent?.Invoke() == false) return;
            _recovery.IsEnabled = false;
            try { await action(); }
            finally { _recovery.IsEnabled = true; }
        };
        Child = new ScrollViewer { MaxHeight = 480, Content = new StackPanel { Children = { _scope, _input, rule, _count, _list, _empty, _recovery } } };
    }

    internal TextBox Input => _input;

    private TextBlock Highlighted(string text, IReadOnlyList<Tern.Search.HighlightRange>? ranges)
    {
        var block = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        var position = 0;
        foreach (var range in (ranges ?? []).OrderBy(r => r.Start))
        {
            if (range.Start < position || range.Length <= 0 || range.Start + range.Length > text.Length) continue;
            if (range.Start > position) block.Inlines!.Add(new Avalonia.Controls.Documents.Run(text[position..range.Start]));
            var run = new Avalonia.Controls.Documents.Run(text.Substring(range.Start, range.Length));
            run[!Avalonia.Controls.Documents.Inline.BackgroundProperty] = this.GetResourceObservable("Tern.AccentSoft").ToBinding();
            run[!Avalonia.Controls.Documents.TextElement.ForegroundProperty] = this.GetResourceObservable("Tern.Text").ToBinding();
            block.Inlines!.Add(run);
            position = range.Start + range.Length;
        }
        if (position < text.Length) block.Inlines!.Add(new Avalonia.Controls.Documents.Run(text[position..]));
        return block;
    }

    internal ListBox Results => _list;
    internal Task QueryWork { get; private set; } = Task.CompletedTask;

    /// <summary>Refresh current rows without accepting one or dismissing the picker.</summary>
    public void RefreshResults() => Refresh(true);

    public void InvalidateScopeIfChanged()
    {
        if (_isCurrent?.Invoke() != false) return;
        _queryGeneration++;
        _queryCancellation?.Cancel();
        PresentError("The workspace changed. Start a new search in the current workspace.");
    }

    public void Open(string watermark, string initialText, Func<string, IReadOnlyList<PickerItem>> provider, Action<PickerItem> accept, Action dismiss, string emptyText = "No matches", Func<string, string>? emptyTextProvider = null,
        Func<string, CancellationToken, Task<IReadOnlyList<PickerItem>>>? asyncProvider = null, Func<bool>? isCurrent = null, Func<string?>? errorProvider = null)
    {
        _queryCancellation?.Cancel();
        _lastQuery = null;
        _asyncProvider = asyncProvider;
        _isCurrent = isCurrent;
        _errorProvider = errorProvider;
        _scope.Text = watermark.TrimEnd('…');
        _recover = null;
        _recovery.IsVisible = false;
        _provider = provider;
        _accept = accept;
        _dismiss = dismiss;
        _emptyText = emptyText;
        _emptyTextProvider = emptyTextProvider;
        _input.PlaceholderText = watermark;
        _input.Text = initialText;
        _input.CaretIndex = initialText.Length;
        Refresh();
        TopLevel.GetTopLevel(this)?.UpdateLayout();
        _input.Focus();
    }

    private void Refresh(bool force = false)
    {
        var query = _input.Text ?? string.Empty;
        if (!force && _lastQuery == query) return;
        _lastQuery = query;
        var generation = ++_queryGeneration;
        _queryCancellation?.Cancel();
        if (_isCurrent?.Invoke() == false) { PresentError("The workspace changed. Start a new search in the current workspace."); return; }
        if (_asyncProvider is { } provider) { QueryWork = RefreshAsync(query, generation, provider); return; }
        try { Present(query, _provider(query)); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { PresentError(ex.Message); }
    }

    public void SetRecoveryAction(string label, Func<Task> action)
    {
        _recovery.Content = label;
        AutomationProperties.SetName(_recovery, label);
        _recover = action;
        _recovery.IsVisible = State == PickerPresentationState.Error;
    }

    private async Task RefreshAsync(string query, long generation, Func<string, CancellationToken, Task<IReadOnlyList<PickerItem>>> provider)
    {
        _queryCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _queryCancellation = cancellation;
        State = PickerPresentationState.Searching;
        _list.ItemsSource = Array.Empty<PickerItem>();
        _list.IsVisible = _count.IsVisible = _recovery.IsVisible = false;
        _empty.Text = "Searching…";
        _empty.IsVisible = true;
        try
        {
            await Task.Delay(100, cancellation.Token);
            var items = await provider(query, cancellation.Token);
            if (!cancellation.IsCancellationRequested && generation == _queryGeneration)
            {
                if (_isCurrent?.Invoke() == false) InvalidateScopeIfChanged();
                else Present(query, items);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        { if (generation == _queryGeneration && !cancellation.IsCancellationRequested) PresentError(ex.Message); }
        finally { if (ReferenceEquals(_queryCancellation, cancellation)) _queryCancellation = null; }
    }

    private void Present(string query, IReadOnlyList<PickerItem> items)
    {
        var error = _errorProvider?.Invoke();
        State = error is not null ? PickerPresentationState.Error : items.Count > 0 ? PickerPresentationState.Results
            : string.IsNullOrWhiteSpace(query) ? PickerPresentationState.EmptyQuery : PickerPresentationState.NoResults;
        _empty.Text = _emptyTextProvider?.Invoke(query) ?? _emptyText;
        if (error is not null) _empty.Text = error;
        _list.ItemsSource = items;
        _list.SelectedIndex = items.Count > 0 ? 0 : -1;
        _empty.IsVisible = items.Count == 0 || error is not null;
        _list.IsVisible = items.Count > 0;
        _count.Text = $"{items.Count} result(s)";
        _count.IsVisible = items.Count > 0;
        _recovery.IsVisible = error is not null && _recover is not null;
    }

    private void PresentError(string error)
    {
        State = PickerPresentationState.Error;
        _list.ItemsSource = Array.Empty<PickerItem>();
        _list.IsVisible = _count.IsVisible = false;
        _empty.Text = "Search unavailable: " + error;
        _empty.IsVisible = true;
        _recovery.IsVisible = _recover is not null;
    }

    private void OnPickerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _dismiss?.Invoke();
            e.Handled = true;
            return;
        }
        if (!_input.IsKeyboardFocusWithin && !ReferenceEquals(e.Source, _input)) return;
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
        if (State is PickerPresentationState.Results or PickerPresentationState.Error && _isCurrent?.Invoke() != false && _list.SelectedItem is PickerItem item)
        {
            _accept?.Invoke(item);
        }
    }
}
