using Avalonia;
using Avalonia.Headless.XUnit;
using Tern.Rendering.Preview;

namespace Tern.PreviewView.Tests;

public sealed class AnchorNavigationTests
{
    [AvaloniaFact]
    public void Explicit_and_encoded_aliases_navigate_to_visible_content()
    {
        var before = string.Join("\n\n", Enumerable.Range(0, 100).Select(i => $"Before {i}"));
        var after = string.Join("\n\n", Enumerable.Range(0, 60).Select(i => $"After {i}"));
        var document = Harness.Build("<a id=\"top\"></a>\n\n# Top\n\n" + before
            + "\n\n<a id=\"europe\" name=\"café\"></a>\n\n## European channels\n\n" + after);
        var (window, view) = Harness.Show(document, 900, 500);
        Assert.True(view.ScrollToAnchor("#europe"));
        Harness.Pump(window);
        Assert.True(view.Scroller.Offset.Y > 500);
        var targetLine = document.FindAnchorSourceLine("europe")!.Value;
        Assert.InRange(view.TopSourceLine, targetLine - 2, targetLine + 2);
        Assert.True(view.ScrollToAnchor("#caf%C3%A9"));
        Assert.True(view.ScrollToAnchor("top"));
        Harness.Pump(window);
        Assert.InRange(view.Scroller.Offset.Y, 0, 80);
        Assert.False(view.ScrollToAnchor("missing"));
        window.Close();
    }

    [AvaloniaFact]
    public void Raw_html_heading_aliases_can_be_navigated()
    {
        var document = Harness.Build("<h1 id=\"how-to-play\">How to play</h1>\n\nBody");
        var (window, view) = Harness.Show(document);
        Assert.True(view.ScrollToAnchor("how-to-play"));
        Assert.Single(document.Outline);
        Assert.Equal("How to play", document.Outline[0].Text);
        window.Close();
    }

    [AvaloniaFact]
    public void An_alias_inside_nested_closed_details_expands_only_its_ancestors()
    {
        var before = string.Join("\n\n", Enumerable.Range(0, 60).Select(i => $"Before {i}"));
        var after = string.Join("\n\n", Enumerable.Range(0, 60).Select(i => $"After {i}"));
        var document = Harness.Build(before + "\n\n<details>\n<summary>Outer</summary>\n\n<details>\n<summary>Inner</summary>\n\n<a id=\"deep\"></a>\n\n## Deep target\n\nBody\n\n</details>\n\n</details>\n\n<details>\n<summary>Unrelated</summary>\n\nHidden\n\n</details>\n\n" + after);
        var (window, view) = Harness.Show(document, 900, 500);
        Assert.True(view.ScrollToAnchor("deep"));
        Harness.Pump(window);
        var details = Harness.All<DetailsView>(view);
        Assert.Equal(2, details.Count(d => d.IsExpanded));
        Assert.Contains(details, d => !d.IsExpanded);
        var heading = Harness.All<Avalonia.Controls.TextBlock>(view).First(t =>
            (Avalonia.Automation.AutomationProperties.GetName(t) ?? "").Contains("Deep target", StringComparison.Ordinal));
        var top = heading.TranslatePoint(default, view.Scroller)!.Value.Y;
        Assert.InRange(top, -5, 80);
        window.Close();
    }
}
