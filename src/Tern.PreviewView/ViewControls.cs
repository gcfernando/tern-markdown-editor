using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Tern.Rendering.Preview;

namespace Tern.PreviewView;

/// <summary>Content that wants to know the width its <see cref="FitScroller"/> may use before it scrolls.</summary>
internal interface IWidthAware
{
    double AvailableWidth { set; }
}

/// <summary>A horizontally scrolling frame that tells its content how much width is really available.</summary>
internal sealed class FitScroller : Border
{
    private readonly ScrollViewer _scroller;
    private readonly Control? _diagramTools;
    private readonly TextBlock? _diagramHint;

    public FitScroller(Control content)
    {
        Content = content;
        _scroller = new ScrollViewer
        {
            AllowAutoHide = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = content,
        };
        if (content is DiagramControl diagram)
        {
            _diagramHint = new TextBlock { Text = "Wide diagram — scroll sideways below", TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 0, 10, 0) };
            PreviewTheme.Bind(_diagramHint, TextBlock.ForegroundProperty, PreviewKeys.Muted);
            var fit = new Button { Content = "Fit width", MinHeight = 32, Padding = new Thickness(10, 4) };
            PreviewTheme.Bind(fit, Button.ForegroundProperty, PreviewKeys.Foreground);
            PreviewTheme.Bind(fit, Button.BackgroundProperty, PreviewKeys.CodeBackground);
            PreviewTheme.Bind(fit, Button.BorderBrushProperty, PreviewKeys.Border);
            AutomationProperties.SetName(fit, "Fit diagram to page width");
            ToolTip.SetTip(fit, "Show the complete diagram, or return to readable size with sideways scrolling.");
            fit.Click += (_, _) =>
            {
                diagram.FitToWidth = !diagram.FitToWidth;
                fit.Content = diagram.FitToWidth ? "Readable size" : "Fit width";
                _diagramHint.Text = diagram.FitToWidth ? "Complete diagram overview" : "Wide diagram — scroll sideways below";
                _scroller.Offset = default;
                InvalidateMeasure();
            };
            var tools = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
            DockPanel.SetDock(fit, Dock.Right); tools.Children.Add(fit); tools.Children.Add(_diagramHint);
            _diagramTools = tools;
            var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
            panel.Children.Add(tools); Grid.SetRow(_scroller, 1); panel.Children.Add(_scroller);
            Child = panel;
        }
        else Child = _scroller;
    }

    public Control Content { get; }

    public ScrollViewer Scroller => _scroller;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Content is IWidthAware aware && double.IsFinite(availableSize.Width))
        {
            aware.AvailableWidth = Math.Max(1, availableSize.Width - BorderThickness.Left - BorderThickness.Right);
        }
        if (_diagramTools is not null && Content is DiagramControl diagram)
            _diagramTools.IsVisible = diagram.DiagramWidth * 0.85 > availableSize.Width;

        return base.MeasureOverride(availableSize);
    }
}

/// <summary>A fenced or indented code block: rounded panel, language label, copy button and selectable monospace text.</summary>
internal sealed class CodeBlockView : Border
{
    private const int MaxDisplayedLines = 1500;
    private const int MaxDisplayedChars = 200_000;

    private readonly string _code;
    private readonly Button _copyButton;
    private readonly TextBlock _copyLabel;
    private readonly DispatcherTimer _resetTimer;

    public CodeBlockView(string code, string? language, double fontSize, Avalonia.Media.FontFamily monoFamily, string? notice = null)
    {
        _code = code;
        Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(0);
        PreviewTheme.Bind(this, BackgroundProperty, PreviewKeys.CodeBackground);
        PreviewTheme.Bind(this, BorderBrushProperty, PreviewKeys.Border);

        var lineCount = CountLines(code);
        AutomationProperties.SetName(this, $"Code block{(Language is null ? string.Empty : ", " + Language)}, {lineCount} {(lineCount == 1 ? "line" : "lines")}");

        var label = new TextBlock
        {
            Text = Language ?? string.Empty,
            FontSize = Math.Max(10, fontSize * 0.78),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        PreviewTheme.Bind(label, TextBlock.ForegroundProperty, PreviewKeys.Muted);
        LanguageLabel = label;

        _copyLabel = new TextBlock { Text = "Copy", FontSize = Math.Max(10, fontSize * 0.78) };
        PreviewTheme.Bind(_copyLabel, TextBlock.ForegroundProperty, PreviewKeys.Muted);
        _copyButton = new Button
        {
            Content = _copyLabel,
            Padding = new Thickness(7, 4),
            MinHeight = 28,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        AutomationProperties.SetName(_copyButton, "Copy code to clipboard");
        ToolTip.SetTip(_copyButton, "Copy code to clipboard");
        _copyButton.Click += async (_, _) => await CopyAsync();
        _resetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _resetTimer.Tick += (_, _) =>
        {
            _resetTimer.Stop();
            _copyLabel.Text = "Copy";
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 6, 6, 0) };
        header.Children.Add(label);
        Grid.SetColumn(_copyButton, 1);
        header.Children.Add(_copyButton);

        var displayed = Displayed(code);
        var codeSize = Math.Max(12, fontSize * 0.85);
        var text = new RichTextBlock
        {
            FontFamily = monoFamily,
            FontSize = codeSize,
            FontWeight = FontWeight.Normal,
            LineHeight = codeSize * 1.45,
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(16, 6, 16, 16),
        };
        PreviewTheme.Bind(text, TextBlock.ForegroundProperty, PreviewKeys.CodeForeground);
        // TextBlock.Text is null when content is supplied as runs; expose the complete displayed text to automation too.
        AutomationProperties.SetName(text, displayed);
        foreach (var token in CodeSyntaxHighlighter.Tokenize(displayed, Language))
        {
            var run = new Avalonia.Controls.Documents.Run(displayed.Substring(token.Start, token.Length));
            text.BindBrush(run, Avalonia.Controls.Documents.TextElement.ForegroundProperty, CodeSyntaxHighlighter.ThemeKey(token.Kind));
            text.Inlines!.Add(run);
        }
        CodeText = text;

        var scroller = new ScrollViewer
        {
            AllowAutoHide = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = text,
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(scroller);
        if (notice is { Length: > 0 })
        {
            var note = new TextBlock { Text = notice, FontSize = fontSize * 0.85, FontStyle = FontStyle.Italic, Margin = new Thickness(14, 0, 14, 8), TextWrapping = TextWrapping.Wrap };
            PreviewTheme.Bind(note, TextBlock.ForegroundProperty, PreviewKeys.Muted);
            stack.Children.Add(note);
        }

        Child = stack;
        DetachedFromVisualTree += (_, _) => _resetTimer.Stop();
    }

    public string? Language { get; }

    public TextBlock LanguageLabel { get; }

    public SelectableTextBlock CodeText { get; }

    public string Code => _code;

    public string CopyButtonText => _copyLabel.Text ?? string.Empty;

    public Button CopyButton => _copyButton;

    /// <summary>Copies the full code (even if the display was capped) to the clipboard and flashes "Copied".</summary>
    public async Task CopyAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        await clipboard.SetTextAsync(_code);
        _copyLabel.Text = "Copied";
        _resetTimer.Stop();
        _resetTimer.Start();
    }

    private static int CountLines(string code)
    {
        if (code.Length == 0)
        {
            return 0;
        }

        var count = 1;
        foreach (var ch in code)
        {
            if (ch == '\n')
            {
                count++;
            }
        }

        return code.EndsWith('\n') ? count - 1 : count;
    }

    private static string Displayed(string code)
    {
        var trimmed = code.TrimEnd('\r', '\n');
        var end = 0;
        var lines = 0;
        while (end < trimmed.Length && end < MaxDisplayedChars)
        {
            var next = trimmed.IndexOf('\n', end);
            lines++;
            if (next < 0)
            {
                return trimmed.Length <= MaxDisplayedChars ? trimmed : trimmed[..MaxDisplayedChars] + "\n… (display truncated; Copy copies everything)";
            }

            if (lines >= MaxDisplayedLines)
            {
                return trimmed[..next] + "\n… (display truncated; Copy copies everything)";
            }

            end = next + 1;
        }

        return trimmed.Length <= MaxDisplayedChars ? trimmed : trimmed[..MaxDisplayedChars] + "\n… (display truncated; Copy copies everything)";
    }
}

/// <summary>A GitHub-style admonition with a coloured bar and title.</summary>
internal sealed class AlertBlock : Border
{
    public AlertBlock(AlertKind kind, Control body, double fontSize)
    {
        Kind = kind;
        var key = kind switch
        {
            AlertKind.Tip => PreviewKeys.AlertTip,
            AlertKind.Important => PreviewKeys.AlertImportant,
            AlertKind.Warning => PreviewKeys.AlertWarning,
            AlertKind.Caution => PreviewKeys.AlertCaution,
            _ => PreviewKeys.AlertNote,
        };

        BorderThickness = new Thickness(4, 0, 0, 0);
        Padding = new Thickness(14, 8, 8, 8);
        CornerRadius = new CornerRadius(0, 6, 6, 0);
        PreviewTheme.Bind(this, BorderBrushProperty, key);
        PreviewTheme.Bind(this, BackgroundProperty, PreviewKeys.CodeBackground);

        TitleText = new TextBlock { Text = kind.ToString(), FontWeight = FontWeight.SemiBold, FontSize = fontSize, Margin = new Thickness(0, 0, 0, 4) };
        PreviewTheme.Bind(TitleText, TextBlock.ForegroundProperty, key);
        AutomationProperties.SetName(this, kind + " alert");

        var stack = new StackPanel();
        stack.Children.Add(TitleText);
        stack.Children.Add(body);
        Child = stack;
    }

    public AlertKind Kind { get; }

    public TextBlock TitleText { get; }
}

/// <summary>A full-width table grid that preserves content minima when sideways scrolling is necessary.</summary>
internal sealed class TablePanel : Panel, IWidthAware
{
    private const double CellPaddingX = 12;
    private const double CellPaddingY = 7;

    private readonly TableLines _lines;
    private double[] _columnWidths = [];
    private double[] _rowHeights = [];
    private double _availableWidth = double.PositiveInfinity;

    public TablePanel(int columns, int rows, int headerRows)
    {
        ColumnCount = columns;
        RowCount = rows;
        HeaderRows = headerRows;
        _lines = new TableLines(this);
        Children.Add(_lines);
    }

    public int CellCount => Children.Count - 1;

    public void AddCell(Control cell) => Children.Add(cell);

    internal IReadOnlyList<double> ColumnWidths => _columnWidths;

    internal IReadOnlyList<double> RowHeights => _rowHeights;

    public int ColumnCount { get; }

    public int RowCount { get; }

    public int HeaderRows { get; }

    public double AvailableWidth
    {
        set
        {
            if (Math.Abs(value - _availableWidth) > 0.5 || double.IsInfinity(_availableWidth))
            {
                _availableWidth = value;
                InvalidateMeasure();
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (ColumnCount == 0 || CellCount == 0)
        {
            return default;
        }

        var natural = new double[ColumnCount];
        var minimum = new double[ColumnCount];
        for (var i = 0; i < CellCount; i++)
        {
            var column = i % ColumnCount;
            var cell = Children[i + 1];
            cell.Measure(Size.Infinity);
            natural[column] = Math.Max(natural[column], cell.DesiredSize.Width);
            cell.Measure(new Size(1, double.PositiveInfinity));
            minimum[column] = Math.Max(minimum[column], cell.DesiredSize.Width);
            // A measure constrained to one pixel clips DesiredSize; preserve explicit content minima for unbroken headings.
            if (cell is Border { Child: { } content } frame)
                minimum[column] = Math.Max(minimum[column], content.MinWidth + frame.Padding.Left + frame.Padding.Right);
        }

        var limit = double.IsFinite(_availableWidth) ? _availableWidth : availableSize.Width;
        var widths = FitColumns(natural, minimum, limit);

        var rows = (CellCount + ColumnCount - 1) / ColumnCount;
        var heights = new double[rows];
        for (var i = 0; i < CellCount; i++)
        {
            var cell = Children[i + 1];
            cell.Measure(new Size(widths[i % ColumnCount], double.PositiveInfinity));
            heights[i / ColumnCount] = Math.Max(heights[i / ColumnCount], cell.DesiredSize.Height);
        }

        _columnWidths = widths;
        _rowHeights = heights;
        _lines.Measure(Size.Infinity);
        _lines.InvalidateVisual();
        return new Size(widths.Sum() + 1, heights.Sum() + 1);
    }

    private static double[] FitColumns(double[] natural, double[] minimum, double limit)
    {
        var naturalTotal = natural.Sum() + 1;
        if (!double.IsFinite(limit))
        {
            return natural;
        }

        if (naturalTotal <= limit)
        {
            // Short labels should not produce a tiny table in a wide reading pane.
            // Give every column the same share of unused space while preserving
            // the extra room required by columns with longer content.
            var extra = (limit - naturalTotal) / natural.Length;
            return natural.Select(width => width + extra).ToArray();
        }

        var minimumTotal = minimum.Sum() + 1;
        if (minimumTotal >= limit)
        {
            return minimum;
        }

        // Share the spare room in proportion to how much each column would like to grow.
        var ratio = (limit - minimumTotal) / (naturalTotal - minimumTotal);
        var widths = new double[natural.Length];
        for (var c = 0; c < widths.Length; c++)
        {
            widths[c] = minimum[c] + ((natural[c] - minimum[c]) * ratio);
        }

        return widths;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _lines.Arrange(new Rect(finalSize));
        double y = 0.5;
        for (var r = 0; r < _rowHeights.Length; r++)
        {
            double x = 0.5;
            for (var c = 0; c < ColumnCount; c++)
            {
                var index = (r * ColumnCount) + c;
                if (index < CellCount)
                {
                    Children[index + 1].Arrange(new Rect(x, y, _columnWidths[c], _rowHeights[r]));
                }

                x += _columnWidths[c];
            }

            y += _rowHeights[r];
        }

        return finalSize;
    }

    /// <summary>Wraps a cell's content with the standard cell padding.</summary>
    public static Border Cell(Control content) => new() { Padding = new Thickness(CellPaddingX, CellPaddingY), Child = content };

    /// <summary>Draws the header background and the 1px grid underneath the cells.</summary>
    private sealed class TableLines(TablePanel owner) : Control
    {
        protected override Size MeasureOverride(Size availableSize) => default;

        public override void Render(DrawingContext context)
        {
            var columns = owner._columnWidths;
            var rows = owner._rowHeights;
            if (columns.Length == 0 || rows.Length == 0)
            {
                return;
            }

            var border = new Pen(PreviewTheme.Resolve(this, PreviewKeys.Border), 1);
            var width = columns.Sum();
            var height = rows.Sum();
            var header = PreviewTheme.Resolve(this, PreviewKeys.TableHeaderBackground);
            double top = 0.5;
            for (var r = 0; r < Math.Min(owner.HeaderRows, rows.Length); r++)
            {
                context.DrawRectangle(header, null, new Rect(0.5, top, width, rows[r]));
                top += rows[r];
            }

            for (var r = owner.HeaderRows; r < rows.Length; r++)
            {
                if ((r - owner.HeaderRows) % 2 == 1)
                {
                    using (context.PushOpacity(0.08))
                        context.DrawRectangle(PreviewTheme.Resolve(this, PreviewKeys.Border), null, new Rect(0.5, top, width, rows[r]));
                }
                top += rows[r];
            }

            double y = 0.5;
            context.DrawLine(border, new Point(0.5, y), new Point(width + 0.5, y));
            foreach (var rowHeight in rows)
            {
                y += rowHeight;
                context.DrawLine(border, new Point(0.5, y), new Point(width + 0.5, y));
            }

            double x = 0.5;
            context.DrawLine(border, new Point(x, 0.5), new Point(x, height + 0.5));
            foreach (var columnWidth in columns)
            {
                x += columnWidth;
                context.DrawLine(border, new Point(x, 0.5), new Point(x, height + 0.5));
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            ((IResourceHost)this).ResourcesChanged += OnResourcesChanged;
            ActualThemeVariantChanged += OnThemeChanged;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            ((IResourceHost)this).ResourcesChanged -= OnResourcesChanged;
            ActualThemeVariantChanged -= OnThemeChanged;
            base.OnDetachedFromVisualTree(e);
        }

        private void OnResourcesChanged(object? sender, ResourcesChangedEventArgs e) => InvalidateVisual();

        private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();
    }
}

/// <summary>A collapsible <c>&lt;details&gt;</c> block; content is built the first time it is opened.</summary>
internal sealed class DetailsView : StackPanel
{
    private readonly Func<Control> _buildContent;
    private readonly TextBlock _glyph;
    private Control? _content;
    private bool _expanded;

    public DetailsView(Control summary, Func<Control> buildContent, bool isOpen)
    {
        _buildContent = buildContent;
        _glyph = new TextBlock { Text = "▸", Width = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        PreviewTheme.Bind(_glyph, TextBlock.ForegroundProperty, PreviewKeys.Accent);
        var toggle = new Button
        {
            Content = _glyph,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinHeight = 0,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        AutomationProperties.SetName(toggle, "Toggle details");
        toggle.Click += (_, _) => IsExpanded = !IsExpanded;

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        header.Children.Add(toggle);
        Grid.SetColumn(summary, 1);
        header.Children.Add(summary);
        Children.Add(header);
        Spacing = 6;
        IsExpanded = isOpen;
    }

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            _glyph.Text = value ? "▾" : "▸";
            if (value && _content is null)
            {
                _content = _buildContent();
                _content.Margin = new Thickness(18, 0, 0, 0);
                Children.Add(_content);
            }

            if (_content is not null)
            {
                _content.IsVisible = value;
            }
        }
    }
}
