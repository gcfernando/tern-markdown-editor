using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Tern.Rendering.Mathematics;
using Tern.Rendering.Mermaid;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.PreviewView;

/// <summary>Builds the Avalonia controls for preview nodes. A failure in one block never escapes; it becomes a placeholder.</summary>
internal sealed class BlockFactory
{
    /// <summary>Source line of a control relative to its top-level block, so reused controls keep correct lines after edits above them.</summary>
    public static readonly AttachedProperty<int> RelativeLineProperty =
        AvaloniaProperty.RegisterAttached<BlockFactory, Control, int>("RelativeLine", -1);

    private static readonly double[] HeadingScales = [2, 1.5, 1.25, 1, 0.875, 0.85];

    private readonly double _fontSize;
    private readonly FontFamily _mono;
    private readonly Action<LinkTarget> _linkInvoked;
    private readonly Func<string, CancellationToken, Task<byte[]?>>? _remoteImageLoader;
    private readonly List<WeakReference<ImageView>> _images = [];
    private int _listDepth;

    public BlockFactory(double fontSize, Action<LinkTarget> linkInvoked, Func<string, CancellationToken, Task<byte[]?>>? remoteImageLoader = null)
    {
        _fontSize = fontSize;
        _linkInvoked = linkInvoked;
        _remoteImageLoader = remoteImageLoader;
        _mono = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, Menlo, monospace");
    }

    public Task ReleaseImages()
    {
        var images = _images.ToArray();
        _images.Clear();
        var completions = new List<Task>();
        foreach (var reference in images)
        {
            if (reference.TryGetTarget(out var image))
            {
                image.Dispose();
                completions.Add(image.LoadingCompletion);
            }
        }

        return Task.WhenAll(completions);
    }

    private ImageView CreateImage(ResourceDecision resolution, string alt, string? title)
    {
        if ((_images.Count & 255) == 255) _images.RemoveAll(reference => !reference.TryGetTarget(out _));
        var image = new ImageView(resolution, alt, title, remoteImageLoader: _remoteImageLoader);
        _images.Add(new WeakReference<ImageView>(image));
        return image;
    }

    public BlockHost CreateHost(BlockSlot slot)
    {
        var node = slot.Node;
        var content = BuildBlock(node, node.Source.StartLine);
        var margin = node switch
        {
            HeadingNode => new Thickness(0, _fontSize * 1.2, 0, _fontSize * 0.45),
            ThematicBreakNode => new Thickness(0, _fontSize * 0.6),
            _ => new Thickness(0, 0, 0, _fontSize * 0.9),
        };

        return new BlockHost(slot) { Child = content, Margin = margin };
    }

    public Control BuildBlock(BlockNode node, int baseLine)
    {
        Control control;
        try
        {
            control = Create(node, baseLine);
        }
#pragma warning disable CA1031 // A malformed node must never take the preview down; it is shown as an error placeholder.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            control = ErrorBlock(node, ex);
        }

        if (node.Alignment != BlockAlignment.None)
        {
            var alignment = node.Alignment switch
            {
                BlockAlignment.Center => TextAlignment.Center,
                BlockAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            };
            // A Markdown container aligns text, not the whole page or its scroll viewport.
            if (control is RichTextBlock rich) rich.TextAlignment = alignment;
            else if (node is HeadingNode && control is Border { Child: RichTextBlock headingText }) headingText.TextAlignment = alignment;
        }
        control.SetValue(RelativeLineProperty, node.Source.StartLine - baseLine);
        return control;
    }

    private Control Create(BlockNode node, int baseLine) => node switch
    {
        HeadingNode heading => Heading(heading),
        ParagraphNode paragraph => Text(paragraph.Inlines, InlineFormat.Default),
        ListNode list => List(list, baseLine),
        BlockQuoteNode quote => Quote(quote, baseLine),
        AlertNode alert => new AlertBlock(alert.Kind, Stack(alert.Blocks, baseLine, 8), _fontSize),
        CodeBlockNode code => new CodeBlockView(code.Code, code.Language, _fontSize, _mono),
        ThematicBreakNode => Rule(),
        TableNode table => Table(table),
        FootnoteSectionNode section => Footnotes(section, baseLine),
        MathBlockNode math => MathBlock(math),
        DiagramNode diagram => Diagram(diagram),
        HtmlSafeBlockNode html => new HtmlBlockRenderer(NewText, _fontSize, _mono, html.Links, html.Images, CreateImage).Render(html.Pieces),
        DetailsNode details => Details(details, baseLine),
        FrontMatterNode front => FrontMatter(front),
        DefinitionListNode definitions => Definitions(definitions, baseLine),
        _ => throw new NotSupportedException($"Unsupported block type {node.GetType().Name}."),
    };

    private RichTextBlock NewText()
    {
        var block = new RichTextBlock { FontSize = _fontSize, LineHeight = _fontSize * 1.5 };
        block.LinkInvoked += (_, e) => _linkInvoked(e.Target);
        return block;
    }

    private Control ErrorBlock(BlockNode node, Exception ex)
    {
        var text = new SelectableTextBlock
        {
            Text = $"This block could not be displayed (line {node.Source.StartLine}): {ex.Message}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = _fontSize * 0.9,
        };
        PreviewTheme.Bind(text, TextBlock.ForegroundProperty, PreviewKeys.Error);
        var frame = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 4), Child = text };
        PreviewTheme.Bind(frame, Border.BorderBrushProperty, PreviewKeys.Error);
        AutomationProperties.SetName(frame, "Block could not be displayed");
        return frame;
    }

    private RichTextBlock Text(IReadOnlyList<InlineNode> inlines, InlineFormat format)
    {
        var block = NewText();
        FillInlines(block, inlines, format);
        return block;
    }

    private void FillInlines(RichTextBlock block, IReadOnlyList<InlineNode> inlines, InlineFormat format)
    {
        var builder = new InlineBuilder(block, block.FontSize, _mono);
        Walk(builder, inlines, format);
        builder.EndLink();
    }

    private void Walk(InlineBuilder builder, IReadOnlyList<InlineNode> nodes, InlineFormat format)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case TextNode text:
                    builder.AddText(text.Text, format);
                    break;
                case EmphasisNode emphasis:
                    Walk(builder, emphasis.Children, format.With(italic: true));
                    break;
                case StrongNode strong:
                    Walk(builder, strong.Children, format.With(bold: true));
                    break;
                case StrikethroughNode strike:
                    Walk(builder, strike.Children, format.With(strike: true));
                    break;
                case CodeSpanNode code:
                    builder.AddText(code.Code, format.With(code: true));
                    break;
                case LinkNode link:
                    builder.BeginLink(new LinkTarget(link.Resolution, link.Destination, 0, link.Title), IsBroken(link.Resolution));
                    Walk(builder, link.Children, format);
                    builder.EndLink();
                    break;
                case ImageNode image:
                    builder.AddControl(CreateImage(image.Resolution, image.Alt, image.Title));
                    break;
                case LineBreakNode lineBreak:
                    if (lineBreak.IsHard)
                    {
                        builder.AddLineBreak();
                    }
                    else
                    {
                        builder.AddText(" ", format);
                    }

                    break;
                case FootnoteRefNode footnote:
                    builder.BeginLink(new LinkTarget(null, footnote.Label, footnote.Number, $"Footnote {footnote.Number}"), broken: false);
                    builder.AddText(footnote.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), format.With(superscript: true));
                    builder.EndLink();
                    break;
                case MathSpanNode math:
                    AddInlineMath(builder, math.Latex, format);
                    break;
                case StyledNode styled:
                    Walk(builder, styled.Children, Styled(format, styled.Style));
                    break;
            }
        }
    }

    // Inline formulas use their Unicode rendering inside the text flow: it wraps, selects and copies like text, whereas an
    // embedded drawing control cannot share a baseline reliably. Display math uses the full MathControl.
    private static void AddInlineMath(InlineBuilder builder, string latex, InlineFormat format)
    {
        var result = MathText.Render(latex);
        builder.AddText(result.IsError ? latex : result.ToPlainUnicode(), result.IsError ? format.With(code: true) : format.With(italic: true));
    }

    private static InlineFormat Styled(InlineFormat format, InlineStyle style) => style switch
    {
        InlineStyle.Kbd => format.With(code: true),
        InlineStyle.Subscript => format.With(subscript: true),
        InlineStyle.Superscript => format.With(superscript: true),
        InlineStyle.Mark => format.With(mark: true),
        InlineStyle.Underline => format.With(underline: true),
        _ => format,
    };

    private static bool IsBroken(ResourceDecision decision) => decision is Blocked or LocalFile { Exists: false };

    private Control Heading(HeadingNode heading)
    {
        var block = NewText();
        block.FontSize = _fontSize * HeadingScales[Math.Clamp(heading.Level, 1, 6) - 1];
        block.FontWeight = FontWeight.SemiBold;
        block.BindBrush(block, TextBlock.ForegroundProperty, PreviewKeys.Heading);
        FillInlines(block, heading.Inlines, InlineFormat.Default);
        AutomationProperties.SetName(block, $"Heading level {heading.Level}: {heading.PlainText}");
        AutomationProperties.SetAutomationId(block, "heading-" + heading.Anchor);
        block.LineHeight = block.FontSize * 1.25;
        if (heading.Level <= 2)
        {
            var frame = new Border { Child = block, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, _fontSize * 0.3) };
            PreviewTheme.Bind(frame, Border.BorderBrushProperty, PreviewKeys.Border);
            return frame;
        }
        return block;
    }

    private StackPanel Stack(IReadOnlyList<BlockNode> blocks, int baseLine, double spacing)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in blocks)
        {
            panel.Children.Add(BuildBlock(child, baseLine));
        }

        return panel;
    }

    private Control Quote(BlockQuoteNode quote, int baseLine)
    {
        var frame = new Border
        {
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(14, 2, 0, 2),
            Child = Stack(quote.Blocks, baseLine, 8),
        };
        PreviewTheme.Bind(frame, Border.BorderBrushProperty, PreviewKeys.QuoteBar);
        return frame;
    }

    private Control List(ListNode list, int baseLine)
    {
        var panel = new StackPanel { Spacing = list.Tight ? 3 : 8 };
        var depth = _listDepth;
        _listDepth++;
        try
        {
            for (var i = 0; i < list.Items.Count; i++)
            {
                var item = list.Items[i];
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                var marker = Marker(list, item, i, depth);
                row.Children.Add(marker);
                var body = Stack(item.Blocks, baseLine, list.Tight ? 2 : 6);
                Grid.SetColumn(body, 1);
                row.Children.Add(body);
                row.SetValue(RelativeLineProperty, item.Source.StartLine - baseLine);
                panel.Children.Add(row);
            }
        }
        finally
        {
            _listDepth--;
        }

        return panel;
    }

    private Control Marker(ListNode list, ListItemNode item, int index, int depth)
    {
        var slot = _fontSize * 1.7;
        if (item.Task != TaskState.None)
        {
            var box = new CheckBox
            {
                IsChecked = item.Task == TaskState.Checked,
                IsHitTestVisible = false,
                Focusable = false,
                MinWidth = 0,
                MinHeight = 0,
                Height = _fontSize * 1.6,
                Padding = default,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Top,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(box, item.Task == TaskState.Checked ? "Completed task" : "Open task");
            return box;
        }

        if (list.Ordered)
        {
            var number = new TextBlock
            {
                Text = (list.StartNumber + index).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".",
                MinWidth = slot,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = _fontSize,
            };
            PreviewTheme.Bind(number, TextBlock.ForegroundProperty, PreviewKeys.Accent);
            return number;
        }

        Shape bullet = depth switch
        {
            0 => new Ellipse { Width = 6, Height = 6 },
            1 => new Ellipse { Width = 6, Height = 6, StrokeThickness = 1.5 },
            _ => new Rectangle { Width = 5, Height = 5 },
        };
        bullet.VerticalAlignment = VerticalAlignment.Top;
        bullet.HorizontalAlignment = HorizontalAlignment.Left;
        if (depth == 1)
        {
            PreviewTheme.Bind(bullet, Shape.StrokeProperty, PreviewKeys.Accent);
        }
        else
        {
            PreviewTheme.Bind(bullet, Shape.FillProperty, PreviewKeys.Accent);
        }

        return new Border
        {
            Width = slot,
            Child = bullet,
            Padding = new Thickness(_fontSize * 0.35, _fontSize * 0.62, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    private Control Rule()
    {
        var rule = new Border { Height = 1 };
        PreviewTheme.Bind(rule, Border.BackgroundProperty, PreviewKeys.Border);
        AutomationProperties.SetName(rule, "Horizontal rule");
        return rule;
    }

    private Control Table(TableNode table)
    {
        var rows = new List<TableRowNode>();
        if (table.Header is not null)
        {
            rows.Add(table.Header);
        }

        rows.AddRange(table.Body);
        var columns = Math.Max(table.Alignments.Count, rows.Count == 0 ? 0 : rows.Max(r => r.Cells.Count));
        var panel = new TablePanel(columns, rows.Count, table.Header is null ? 0 : 1);
        foreach (var row in rows)
        {
            for (var c = 0; c < columns; c++)
            {
                if (c >= row.Cells.Count)
                {
                    panel.AddCell(TablePanel.Cell(new TextBlock()));
                    continue;
                }

                var cell = row.Cells[c];
                var text = Text(cell.Inlines, row.IsHeader ? InlineFormat.Default.With(bold: true) : InlineFormat.Default);
                text.FontSize = _fontSize;
                text.LineHeight = text.FontSize * 1.5;
                text.TextWrapping = TextWrapping.WrapWithOverflow;
                // Body labels need the same word-preserving minimum as headers.
                // Otherwise proportional columns can split short words such as
                // "Containers" even when the table fits comfortably in the page.
                {
                    var label = string.Concat(text.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
                    var words = label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var word in words)
                    {
                        var measured = new FormattedText(word.Length > 128 ? word[..128] : word, System.Globalization.CultureInfo.CurrentCulture,
                            Avalonia.Media.FlowDirection.LeftToRight, new Typeface(text.FontFamily, FontStyle.Normal, row.IsHeader ? FontWeight.SemiBold : FontWeight.Normal), text.FontSize, Brushes.Black);
                        text.MinWidth = Math.Max(text.MinWidth, Math.Min(320, Math.Ceiling(measured.WidthIncludingTrailingWhitespace) + 2));
                    }
                }
                var alignment = cell.Alignment != TableAlignment.None ? cell.Alignment : c < table.Alignments.Count ? table.Alignments[c] : TableAlignment.None;
                text.TextAlignment = alignment switch
                {
                    TableAlignment.Center => TextAlignment.Center,
                    TableAlignment.Right => TextAlignment.Right,
                    _ => TextAlignment.Left,
                };
                panel.AddCell(TablePanel.Cell(text));
            }
        }

        AutomationProperties.SetName(panel, $"Table, {rows.Count} rows, {columns} columns");
        var frame = new FitScroller(panel) { HorizontalAlignment = HorizontalAlignment.Stretch };
        return frame;
    }

    private Control Footnotes(FootnoteSectionNode section, int baseLine)
    {
        var panel = new StackPanel { Spacing = 6 };
        var title = new TextBlock { Text = "Footnotes", FontWeight = FontWeight.SemiBold, FontSize = _fontSize * 0.9 };
        PreviewTheme.Bind(title, TextBlock.ForegroundProperty, PreviewKeys.Muted);
        panel.Children.Add(title);
        foreach (var item in section.Items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var number = new TextBlock { Text = item.Number + ".", MinWidth = _fontSize * 1.6, Margin = new Thickness(0, 0, 6, 0), TextAlignment = TextAlignment.Right, FontSize = _fontSize * 0.92 };
            PreviewTheme.Bind(number, TextBlock.ForegroundProperty, PreviewKeys.Muted);
            row.Children.Add(number);
            var body = Stack(item.Blocks, baseLine, 4);
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            row.SetValue(RelativeLineProperty, item.Source.StartLine - baseLine);
            AutomationProperties.SetName(row, "Footnote " + item.Number);
            panel.Children.Add(row);
        }

        var frame = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0), Child = panel };
        PreviewTheme.Bind(frame, Border.BorderBrushProperty, PreviewKeys.Border);
        return frame;
    }

    private Control MathBlock(MathBlockNode node)
    {
        var result = MathText.Render(node.Latex);
        if (result.IsError)
        {
            return SourceBox(node.Latex, "Math error: " + result.ErrorMessage);
        }

        var math = new MathControl { MathFontSize = _fontSize * 1.25, Result = result, HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(math, "Math formula: " + node.Latex);
        var host = new FitScroller(math) { HorizontalAlignment = HorizontalAlignment.Center };
        var wrapper = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        wrapper.Children.Add(host);
        return wrapper;
    }

    private Control SourceBox(string source, string message)
    {
        var panel = new StackPanel { Spacing = 4 };
        var text = new SelectableTextBlock { Text = source, FontFamily = _mono, FontSize = _fontSize * 0.9, TextWrapping = TextWrapping.Wrap };
        PreviewTheme.Bind(text, TextBlock.ForegroundProperty, PreviewKeys.CodeForeground);
        panel.Children.Add(text);
        var note = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = _fontSize * 0.85 };
        PreviewTheme.Bind(note, TextBlock.ForegroundProperty, PreviewKeys.Error);
        panel.Children.Add(note);
        var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(14, 10), Child = panel };
        PreviewTheme.Bind(frame, Border.BackgroundProperty, PreviewKeys.CodeBackground);
        PreviewTheme.Bind(frame, Border.BorderBrushProperty, PreviewKeys.Border);
        AutomationProperties.SetName(frame, message);
        return frame;
    }

    private Control Diagram(DiagramNode node)
    {
        if (!node.DiagramType.Equals("mermaid", StringComparison.OrdinalIgnoreCase))
        {
            return new CodeBlockView(node.DiagramSource, node.DiagramType, _fontSize, _mono, $"Diagram type not supported offline — showing source ({node.DiagramType}).");
        }

        if (MermaidDiagram.TryParse(node.DiagramSource, out var parsed))
        {
            var diagram = new DiagramControl(parsed);
            AutomationProperties.SetName(diagram, $"{diagram.Description} with {diagram.ElementCount} elements");
            var frame = new FitScroller(diagram) { HorizontalAlignment = HorizontalAlignment.Left };
            return frame;
        }

        var notice = parsed.Kind == MermaidResultKind.Unsupported
            ? $"Diagram type not supported offline — showing source ({parsed.UnsupportedType})."
            : $"Diagram could not be drawn{(parsed.ErrorLine > 0 ? $" (line {parsed.ErrorLine})" : string.Empty)}: {parsed.ErrorMessage} Showing source.";
        return new CodeBlockView(node.DiagramSource, "mermaid", _fontSize, _mono, notice);
    }

    private Control Details(DetailsNode details, int baseLine)
    {
        var summary = Text(details.Summary, InlineFormat.Default.With(bold: true));
        if (details.Summary.Count == 0)
        {
            summary.Text = "Details";
        }

        return new DetailsView(summary, () => Stack(details.Blocks, baseLine, 8), details.IsOpen);
    }

    private Control FrontMatter(FrontMatterNode front)
    {
        if (front.Properties.Count == 0)
        {
            return new CodeBlockView(front.Raw, "yaml", _fontSize, _mono);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        for (var i = 0; i < front.Properties.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var key = new TextBlock { Text = front.Properties[i].Key, FontWeight = FontWeight.SemiBold, FontSize = _fontSize, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
            PreviewTheme.Bind(key, TextBlock.ForegroundProperty, PreviewKeys.Foreground);
            var value = new SelectableTextBlock { Text = front.Properties[i].Value, TextWrapping = TextWrapping.Wrap, FontSize = _fontSize, LineHeight = _fontSize * 1.5 };
            PreviewTheme.Bind(value, TextBlock.ForegroundProperty, PreviewKeys.Foreground);
            var keyCell = new Border { Child = key, MinWidth = _fontSize * 6.5, Padding = new Thickness(12, 6), BorderThickness = new Thickness(0, 0, 1, i + 1 < front.Properties.Count ? 1 : 0) };
            var valueCell = new Border { Child = value, Padding = new Thickness(12, 6), BorderThickness = new Thickness(0, 0, 0, i + 1 < front.Properties.Count ? 1 : 0) };
            PreviewTheme.Bind(keyCell, Border.BorderBrushProperty, PreviewKeys.Border);
            PreviewTheme.Bind(valueCell, Border.BorderBrushProperty, PreviewKeys.Border);
            if ((i & 1) != 0)
            {
                PreviewTheme.Bind(keyCell, Border.BackgroundProperty, PreviewKeys.CodeBackground);
                PreviewTheme.Bind(valueCell, Border.BackgroundProperty, PreviewKeys.CodeBackground);
            }
            Grid.SetRow(keyCell, i);
            Grid.SetRow(valueCell, i);
            Grid.SetColumn(valueCell, 1);
            grid.Children.Add(keyCell);
            grid.Children.Add(valueCell);
        }

        var frame = new Border { BorderThickness = new Thickness(1), Child = grid };
        PreviewTheme.Bind(frame, Border.BorderBrushProperty, PreviewKeys.Border);
        AutomationProperties.SetName(frame, "Document properties");
        return frame;
    }

    private Control Definitions(DefinitionListNode list, int baseLine)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var item in list.Items)
        {
            var entry = new StackPanel { Spacing = 3 };
            foreach (var term in item.Terms)
            {
                entry.Children.Add(Text(term, InlineFormat.Default.With(bold: true)));
            }

            var definitions = Stack(item.Definitions, baseLine, 4);
            definitions.Margin = new Thickness(24, 0, 0, 0);
            entry.Children.Add(definitions);
            entry.SetValue(RelativeLineProperty, item.Source.StartLine - baseLine);
            panel.Children.Add(entry);
        }

        return panel;
    }
}
