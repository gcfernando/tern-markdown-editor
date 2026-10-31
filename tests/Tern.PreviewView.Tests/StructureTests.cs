using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Tern.Rendering.Preview;

namespace Tern.PreviewView.Tests;

public class StructureTests
{
    private const string Rich = """
        ---
        title: Sample
        ---

        # Title

        ## Section

        Paragraph with **bold**, *italic*, ~~strike~~ and `code` plus a [link](#section).

        - one
        - two
        - [x] done
        - [ ] todo

        1. first
        2. second

        > quoted text

        > [!NOTE]
        > Note body

        > [!TIP]
        > Tip body

        > [!WARNING]
        > Warning body

        ```csharp
        var x = 1;
        ```

        ---

        | A | B |
        |---|---|
        | 1 | 2 |
        | 3 | 4 |

        Footnote here[^1].

        [^1]: The note.
        """;

    [AvaloniaFact]
    public void Headings_AreRenderedWithAutomationNames()
    {
        var (window, view) = Harness.Show(Harness.Build(Rich));
        var headings = Harness.All<TextBlock>(view)
            .Where(t => (Avalonia.Automation.AutomationProperties.GetName(t) ?? "").StartsWith("Heading level", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, headings.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void Table_HasExpectedGridDimensions()
    {
        var (window, view) = Harness.Show(Harness.Build(Rich));
        var table = Assert.Single(Harness.All<TablePanel>(view));
        Assert.Equal(2, table.ColumnCount);
        Assert.Equal(3, table.RowCount);
        Assert.Equal(1, table.HeaderRows);
        Assert.Equal(6, table.CellCount);
        Assert.Equal(2, table.ColumnWidths.Count);
        Assert.All(table.ColumnWidths, w => Assert.True(w > 20));
        window.Close();
    }

    [AvaloniaFact]
    public void Table_headings_keep_words_intact_in_a_narrow_preview()
    {
        var (window, view) = Harness.Show(Harness.Build("| Component | Responsibility | Key Technologies |\n|---|---|---|\n| Workspace | File management and indexing | .NET, SQLite |\n"), 500, 500);
        var heading = Assert.Single(Harness.All<RichTextBlock>(view), block => Harness.PlainText(block) == "Component");
        Assert.Single(heading.TextLayout.TextLines);
        window.Close();
    }

    [AvaloniaFact]
    public void Table_body_subject_words_remain_intact_at_the_normal_reading_width()
    {
        const string markdown = "| Subject | Preparation |\n|---|---|\n| Python | Use the guide's Python version and a virtual environment; some special methods require newer versions |\n| C#/.NET | Install the SDK required by the selected example; check version-specific syntax |\n| SQL | Choose SQL Server or MySQL and create a disposable practice database |\n| Containers | Install the tool and inspect the recipe before using it for running commands |\n| Azure | Have an authorized subscription and permissions, plus a cost plan for live resources |\n| Local AI | Follow the Windows/PowerShell, Python and Ollama setup in the lab |\n";
        var (window, view) = Harness.Show(Harness.Build(markdown), 1200, 900);
        try
        {
            var subject = Assert.Single(Harness.All<RichTextBlock>(view), block => Harness.PlainText(block) == "Containers");
            Assert.Single(subject.TextLayout.TextLines);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CodeBlock_HasLanguageLabelAndCopyButton()
    {
        var (window, view) = Harness.Show(Harness.Build(Rich));
        var code = Assert.Single(Harness.All<CodeBlockView>(view));
        Assert.Equal("csharp", code.LanguageLabel.Text);
        Assert.Contains("var x = 1;", Harness.PlainText(code.CodeText));
        Assert.Equal("Copy", code.CopyButtonText);
        Assert.True(code.CopyButton.IsVisible);
        Assert.False(string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(code)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task CopyButton_PutsCodeOnClipboardAndShowsCopied()
    {
        var (window, view) = Harness.Show(Harness.Build(Rich));
        var code = Assert.Single(Harness.All<CodeBlockView>(view));
        await code.CopyAsync();
        Assert.Equal("Copied", code.CopyButtonText);
        var clipboard = TopLevel.GetTopLevel(view)?.Clipboard;
        Assert.NotNull(clipboard);
        var text = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard);
        Assert.Equal(code.Code, text);
        window.Close();
    }

    [AvaloniaFact]
    public void TaskList_ShowsReadOnlyCheckBoxes()
    {
        var (window, view) = Harness.Show(Harness.Build(Rich));
        var boxes = Harness.All<CheckBox>(view);
        Assert.Equal(2, boxes.Count);
        Assert.Equal(1, boxes.Count(b => b.IsChecked == true));
        Assert.All(boxes, b => Assert.False(b.IsHitTestVisible));
        window.Close();
    }

    [AvaloniaFact]
    public void Alerts_ExposeTheirKinds()
    {
        var (window, view) = Harness.Show(Harness.Build(Rich));
        var kinds = Harness.All<AlertBlock>(view).Select(a => a.Kind).ToList();
        Assert.Equal(3, kinds.Count);
        Assert.Equal(3, kinds.Distinct().Count());
        Assert.All(Harness.All<AlertBlock>(view), a => Assert.False(string.IsNullOrWhiteSpace(a.TitleText.Text)));
        window.Close();
    }

    [AvaloniaFact]
    public void Footnotes_AreRendered()
    {
        var (window, view) = Harness.Show(Harness.Build(Rich));
        Assert.Contains(Harness.All<TextBlock>(view), t => (t.Text ?? "").Contains("The note.", StringComparison.Ordinal)
            || t.Inlines?.Any(i => i is Avalonia.Controls.Documents.Run r && (r.Text ?? "").Contains("The note.", StringComparison.Ordinal)) == true);
        window.Close();
    }

    [AvaloniaFact]
    public void Truncation_ShowsBanner()
    {
        var doc = Harness.Build(string.Join("\n\n", Enumerable.Range(0, 200).Select(i => $"Paragraph {i} with *words*")), new PreviewOptions { MaxNodes = 50 });
        Assert.True(doc.Truncated);
        var (window, view) = Harness.Show(doc);
        Assert.True(view.Banner.IsVisible);
        Assert.StartsWith("Preview truncated: ", view.BannerText.Text, StringComparison.Ordinal);
        Assert.EndsWith(". Edit continues to work.", view.BannerText.Text, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void BadBlock_IsIsolatedAndOthersStillRender()
    {
        var good = Harness.Build("# Before\n\ntext\n");
        var bad = new HeadingNode(new SourceLocation(3, 3, 10, 14), 1, "x", "x", null!);
        var blocks = new List<BlockNode>(good.Blocks) { bad };
        var doc = (PreviewDocument)Activator.CreateInstance(
            typeof(PreviewDocument),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null,
            [blocks, good.Outline, TruncationReason.None, good.NodeCount, good.SourceLength, good.LineCount, null, null],
            null)!;
        var (window, view) = Harness.Show(doc);
        Assert.NotEmpty(Harness.All<TextBlock>(view));
        Assert.Contains(Harness.All<TextBlock>(view), t => (t.Text ?? "").Contains("could not be displayed", StringComparison.OrdinalIgnoreCase));
        window.Close();
    }

    [AvaloniaFact]
    public void ThemeSwap_ChangesBrushes()
    {
        var (window, view) = Harness.Show(Harness.Build("# Hello\n\n| a |\n|---|\n| b |\n"));
        var before = Assert.IsAssignableFrom<ISolidColorBrush>(PreviewTheme.Resolve(view, PreviewKeys.Foreground)).Color;
        Application.Current!.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(PreviewTheme.Paper());
        Harness.Pump(window);
        var after = Assert.IsAssignableFrom<ISolidColorBrush>(PreviewTheme.Resolve(view, PreviewKeys.Foreground)).Color;
        Assert.NotEqual(before, after);
        var heading = Harness.All<TextBlock>(view).First(t => (Avalonia.Automation.AutomationProperties.GetName(t) ?? "").StartsWith("Heading", StringComparison.Ordinal));
        Assert.Equal(((ISolidColorBrush)PreviewTheme.Resolve(view, PreviewKeys.Heading)).Color, ((ISolidColorBrush)heading.Foreground!).Color);
        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(PreviewTheme.Midnight());
        window.Close();
    }
}
