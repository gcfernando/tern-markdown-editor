using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Tern.Desktop.Themes;
using Tern.Domain.Merging;

namespace Tern.Desktop.Views;

/// <summary>Two read-only source panes with changed lines highlighted, a change count and Previous/Next change navigation (P-04).</summary>
internal sealed class DiffView : Grid
{
    private readonly SelectableTextBlock _leftText = Text();
    private readonly SelectableTextBlock _rightText = Text();
    private readonly ScrollViewer _leftScroll;
    private readonly ScrollViewer _rightScroll;
    private readonly TextBlock _leftLabel = new() { FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _rightLabel = new() { FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _previous = new() { Content = "Previous change" };
    private readonly Button _next = new() { Content = "Next change" };
    private readonly string _leftHighlightKey;
    private readonly string _rightHighlightKey;
    private DiffMap _map = DiffMap.Compute(string.Empty, string.Empty);
    private int _current = -1;

    public DiffView(string leftLabel, string rightLabel, bool leftIsRemoval = true)
    {
        RowDefinitions = new RowDefinitions("Auto,*");
        this[!RowSpacingProperty] = GuideParts.Space(8).ToBinding();
        _leftLabel.Text = leftLabel;
        _rightLabel.Text = rightLabel;
        _leftHighlightKey = leftIsRemoval ? ThemeManager.ErrorBackground : ThemeManager.SuccessBackground;
        _rightHighlightKey = ThemeManager.SuccessBackground;
        _leftScroll = Scroller(_leftText, leftLabel);
        _rightScroll = Scroller(_rightText, rightLabel);
        AutomationProperties.SetName(_previous, "Go to previous change");
        AutomationProperties.SetName(_next, "Go to next change");
        _previous.Click += (_, _) => Step(-1);
        _next.Click += (_, _) => Step(1);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, [!StackPanel.SpacingProperty] = GuideParts.Space(8).ToBinding(), Children = { _previous, _next } };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(12).ToBinding() };
        header.Children.Add(_summary);
        Grid.SetColumn(bar, 1);
        header.Children.Add(bar);
        Children.Add(header);

        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(12).ToBinding() };
        panes.Children.Add(Labeled(_leftLabel, _leftScroll, 0));
        panes.Children.Add(Labeled(_rightLabel, _rightScroll, 1));
        SetRow(panes, 1);
        Children.Add(panes);
        _leftScroll.ScrollChanged += (_, _) => Mirror(_leftScroll, _rightScroll);
        _rightScroll.ScrollChanged += (_, _) => Mirror(_rightScroll, _leftScroll);
    }

    public string LeftLabel { get => _leftLabel.Text ?? string.Empty; set => _leftLabel.Text = value; }
    public string LeftSource { get; private set; } = string.Empty;
    public string RightSource { get; private set; } = string.Empty;
    public int ChangeCount => _map.Blocks.Count;
    public string SummaryText => _summary.Text ?? string.Empty;
    public bool CanStep => _map.Blocks.Count > 0;

    /// <summary>Replaces both sides; <paramref name="leftOverride"/> shows a message instead of the left text (for example an unreadable version).</summary>
    public void Show(string? left, string right, string? leftOverride = null)
    {
        _current = -1;
        LeftSource = leftOverride ?? left ?? string.Empty;
        RightSource = right;
        _map = DiffMap.Compute(left ?? string.Empty, right);
        Fill(_leftText, _map.LeftLines, _map.Analyzed && leftOverride is null ? _map.LeftChanged : null, _leftHighlightKey, leftOverride);
        Fill(_rightText, _map.RightLines, _map.Analyzed && leftOverride is null ? _map.RightChanged : null, _rightHighlightKey, null);
        _previous.IsEnabled = _next.IsEnabled = _map.Blocks.Count > 0;
        _summary.Text = leftOverride is not null ? string.Empty
            : !_map.Analyzed ? "These files are too large to compare line by line; the panes show plain text."
            : _map.Blocks.Count == 0 ? "The two versions are identical."
            : $"{_map.Blocks.Count} change{(_map.Blocks.Count == 1 ? string.Empty : "s")}: {_map.LeftChangedCount} line{(_map.LeftChangedCount == 1 ? string.Empty : "s")} only on the left, {_map.RightChangedCount} only on the right.";
    }

    public void Step(int direction)
    {
        if (_map.Blocks.Count == 0) return;
        _current = (_current + direction + _map.Blocks.Count) % _map.Blocks.Count;
        var block = _map.Blocks[_current];
        _summary.Text = $"Change {_current + 1} of {_map.Blocks.Count} · {_map.LeftChangedCount} line(s) only on the left, {_map.RightChangedCount} only on the right.";
        ScrollTo(_leftText, _leftScroll, block.LeftStart, _map.LeftLines.Length);
        ScrollTo(_rightText, _rightScroll, block.RightStart, _map.RightLines.Length);
    }

    private static void ScrollTo(SelectableTextBlock text, ScrollViewer scroller, int line, int lines)
    {
        if (lines == 0 || text.Bounds.Height <= 0) return;
        scroller.Offset = new Vector(scroller.Offset.X, Math.Max(0, text.Bounds.Height * Math.Min(line, lines - 1) / lines - 12));
    }

    private bool _mirroring;

    private void Mirror(ScrollViewer from, ScrollViewer to)
    {
        if (_mirroring) return;
        _mirroring = true;
        try { to.Offset = new Vector(to.Offset.X, from.Offset.Y); }
        finally { _mirroring = false; }
    }

    private static SelectableTextBlock Text() => new()
    {
        FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
        Padding = new Thickness(8),
    };

    private static ScrollViewer Scroller(Control content, string name)
    {
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content,
        };
        AutomationProperties.SetName(scroller, name);
        return scroller;
    }

    private static Grid Labeled(TextBlock label, ScrollViewer scroller, int column)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), [!Grid.RowSpacingProperty] = GuideParts.Space(8).ToBinding() };
        grid.Children.Add(label);
        var frame = new Border { BorderThickness = new Thickness(1), [!Border.CornerRadiusProperty] = GuideParts.Radius("Control").ToBinding(), Child = scroller };
        frame.Bind(Border.BorderBrushProperty, scroller.GetResourceObservable(ThemeManager.ControlBorder));
        SetRow(frame, 1);
        grid.Children.Add(frame);
        SetColumn(grid, column);
        return grid;
    }

    private static void Fill(SelectableTextBlock block, string[] lines, bool[]? changed, string highlightKey, string? message)
    {
        block.Inlines!.Clear();
        if (message is not null) { block.Inlines.Add(new Run(message)); return; }
        IBrush? highlight = null;
        if (changed is not null)
        {
            highlight = Application.Current is { } app && app.TryGetResource(highlightKey, app.ActualThemeVariant, out var value) ? value as IBrush : null;
            highlight ??= Brushes.Transparent;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r', '\n');
            var run = new Run(line.Length == 0 ? " " : line);
            if (highlight is not null && changed![i]) run.Background = highlight;
            block.Inlines.Add(run);
            if (i < lines.Length - 1) block.Inlines.Add(new LineBreak());
        }
    }
}
