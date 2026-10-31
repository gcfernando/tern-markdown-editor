using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;

namespace Tern.PreviewView.Tests;

public sealed class FullWidthGeometryTests
{
    private const string Paragraph = "A normal-sized paragraph with **emphasis**, a [link](#section), and enough ordinary words to wrap naturally as the available pane width changes. ";

    [AvaloniaTheory]
    [InlineData(440)]
    [InlineData(900)]
    [InlineData(1600)]
    public void Full_width_flows_to_pane_edges_without_rescaling_type(double width)
    {
        var (window, view) = Harness.Show(Harness.Build("# Section\n\n" + string.Concat(Enumerable.Repeat(Paragraph, 4))), width, 700);
        try
        {
            view.FontSize = 16;
            view.ReadingWidth = 300; // The optional centered-column cap must not constrain full-width mode.
            Harness.Pump(window);
            Assert.True(view.UseFullWidth);
            Assert.Equal(16, view.FontSize);
            AssertFullWidth(view);
            var paragraph = Harness.All<RichTextBlock>(view).First(block => Harness.PlainText(block).StartsWith("A normal-sized", StringComparison.Ordinal));
            Assert.Equal(16, paragraph.FontSize);
            Assert.True(paragraph.Bounds.Width >= view.Panel.Bounds.Width - 2);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Centered_reading_width_remains_an_explicit_option()
    {
        var (window, view) = Harness.Show(Harness.Build("# Section\n\n" + Paragraph), 1400, 600);
        try
        {
            view.FontSize = 16;
            view.UseFullWidth = false; view.ReadingWidth = 420;
            Harness.Pump(window);
            Assert.InRange(view.Panel.Bounds.Width, 419, 421);
            Assert.True(view.Panel.TranslatePoint(default, view.Scroller)!.Value.X > 300);
            Assert.Equal(16, view.FontSize);
            view.ReadingWidth = 620; Harness.Pump(window);
            Assert.InRange(view.Panel.Bounds.Width, 619, 621);
            view.UseFullWidth = true; Harness.Pump(window);
            AssertFullWidth(view);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Split_to_reading_and_back_uses_the_new_pane_width_with_unchanged_font()
    {
        var view = new PreviewView { FontSize = 16, Document = Harness.Build("# Section\n\n" + string.Concat(Enumerable.Repeat(Paragraph, 3))) };
        var source = new Border();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Children = { source, view } };
        Grid.SetColumn(view, 1);
        var window = new Window { Width = 1400, Height = 600, Content = grid };
        try
        {
            window.Show(); Harness.Pump(window);
            var splitWidth = view.Panel.Bounds.Width;
            AssertFullWidth(view);
            grid.ColumnDefinitions[0].Width = new GridLength(0); source.IsVisible = false;
            Harness.Pump(window);
            Assert.True(view.Panel.Bounds.Width > splitWidth + 600);
            AssertFullWidth(view); Assert.Equal(16, view.FontSize);
            grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star); source.IsVisible = true;
            Harness.Pump(window);
            Assert.InRange(view.Panel.Bounds.Width, splitWidth - 1, splitWidth + 1);
            AssertFullWidth(view); Assert.Equal(16, view.FontSize);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Width_mode_changes_and_explicit_font_zoom_preserve_the_visible_source_section()
    {
        var markdown = string.Concat(Enumerable.Range(1, 100).Select(i => $"# Section {i}\n\n{Paragraph}{Paragraph}\n\n"));
        var line = 1 + markdown.AsSpan(0, markdown.IndexOf("# Section 50\n", StringComparison.Ordinal)).Count('\n');
        var (window, view) = Harness.Show(Harness.Build(markdown), 1500, 600);
        try
        {
            view.FontSize = 16;
            Harness.Pump(window);
            view.ScrollToSourceLine(line); Harness.Pump(window, 8);
            Assert.InRange(view.TopSourceLine, line - 1, line + 1);
            var scrollFeedback = 0;
            view.TopSourceLineChanged += (_, _) => scrollFeedback++;
            view.ReadingWidth = 420; view.UseFullWidth = false; Harness.Pump(window, 12);
            Assert.InRange(view.TopSourceLine, line - 1, line + 1);
            Assert.Equal(0, scrollFeedback);
            view.UseFullWidth = true; Harness.Pump(window, 12);
            Assert.InRange(view.TopSourceLine, line - 1, line + 1);
            Assert.Equal(16, view.FontSize);
            view.FontSize = 20; Harness.Pump(window, 12);
            Assert.Equal(20, view.FontSize);
            Assert.InRange(view.TopSourceLine, line - 1, line + 1);
            AssertFullWidth(view);
        }
        finally { window.Close(); }
    }

    private static void AssertFullWidth(PreviewView view)
    {
        var origin = view.Panel.TranslatePoint(default, view.Scroller)!.Value;
        var remaining = view.Scroller.Viewport.Width - origin.X - view.Panel.Bounds.Width;
        Assert.InRange(origin.X, 15, 17);
        Assert.InRange(remaining, 15, 17);
        Assert.True(view.Panel.Bounds.Width > 0);
    }
}
