using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Tern.App.Settings;
using Tern.App.Commands;
using Tern.Desktop.Editing;
using System.Reflection;
using AvaloniaEdit.Document;

namespace Tern.Desktop.Tests;

public sealed class FencedCodeHighlightingTests
{
    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper, false)]
    [InlineData(ThemeChoice.Midnight, true)]
    [InlineData(ThemeChoice.Paper, true)]
    [InlineData(ThemeChoice.Midnight, false)]
    public void Declared_language_is_visible_in_source_and_hybrid_without_changing_the_draft(ThemeChoice theme, bool hybrid)
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.Theme = theme; f.Shell.Settings.HybridSourceEnabled = hybrid;
        f.Window.ApplySettings(); f.Window.ApplyTheme();
        const string original = "# Code\n\n```csharp\nvar x = 35;\n```\n\nThe draft stays exact.\n";
        var tab = f.Shell.NewDocument(original);
        Pump(f.Window);
        var line = f.Window.EditorControl.TextArea.TextView.VisualLines.Single(item => item.FirstDocumentLine.LineNumber == 4);
        var colours = line.Elements.Select(item => item.TextRunProperties.ForegroundBrush?.ToString()).Distinct().ToArray();
        Assert.True(colours.Length >= 3, "var, plain identifiers and numeric literals must have distinct lexical colours.");
        Assert.Equal(original, tab.CurrentText);
        Assert.Equal(original, f.Window.EditorControl.Text);
        if (theme == ThemeChoice.Paper && hybrid || theme == ThemeChoice.Midnight && !hybrid)
        {
            f.Shell.Settings.UiScale = 1.5; f.Window.Width = 900; f.Window.Height = 650;
            f.Window.ApplySettings(); Pump(f.Window);
        }
        f.Shell.NewDocument("# Declared code languages\n\n```csharp\nvar x = 35; // a number\nvar text = @\"first\nsecond\";\n```\n\n```sql\nSELECT name FROM notes;\n```\n\n```python\nclass Note:\n\tdef title(self):\n\t\treturn f\"{self.name}\"\n```\n\n```unknown\nplain text stays plain\n```\n");
        Pump(f.Window);
        Capture(f.Window, $"source-{theme}-hybrid{hybrid}-top");
        f.Window.EditorControl.ScrollToLine(15); Pump(f.Window);
        Capture(f.Window, $"source-{theme}-hybrid{hybrid}-lower");
    }

    [AvaloniaFact]
    public async Task Language_edits_multiline_boundaries_strict_closing_and_undo_keep_source_identity()
    {
        using var f = new DesktopFixture();
        const string original = "```csharp\nvar x = 35;\n/* comment\nreturn x;\n*/ return x;\n```not-a-close\nvar y = 36;\n```\nreturn plain;\n";
        var tab = f.Shell.NewDocument(original); Pump(f.Window);
        var editor = f.Window.EditorControl;
        Assert.NotEqual(Colour(editor, 2, 0), Colour(editor, 4, 0));
        Assert.Equal(Colour(editor, 4, 0), Colour(editor, 3, 0));
        Assert.Equal(Colour(editor, 2, 0), Colour(editor, 7, 0));
        editor.Document.Replace(original.IndexOf("csharp", StringComparison.Ordinal), 6, "unknown"); Pump(f.Window);
        var unknownLine = editor.TextArea.TextView.VisualLines.Single(item => item.FirstDocumentLine.LineNumber == 2);
        Assert.Single(unknownLine.Elements.Select(item => item.TextRunProperties.ForegroundBrush?.ToString()).Distinct());
        await f.Window.RunCommandAsync(CommandIds.Undo); Pump(f.Window);
        Assert.Equal(original, tab.CurrentText);
        Assert.NotEqual(Colour(editor, 2, 0), Colour(editor, 2, 8));
        var close = editor.Document.GetLineByNumber(3);
        editor.Document.Remove(close.Offset, 2); Pump(f.Window);
        Assert.Equal(Colour(editor, 2, 0), Colour(editor, 4, 0));
        await f.Window.RunCommandAsync(CommandIds.Undo); Pump(f.Window);
        Assert.Equal(original, tab.CurrentText);
        Assert.Equal(Colour(editor, 4, 0), Colour(editor, 3, 0));
    }

    [Fact]
    public void Incremental_lexical_state_propagates_only_until_it_converges_and_long_lines_remain_bounded()
    {
        var text = new TextDocument("```csharp\n" + string.Join('\n', Enumerable.Repeat("var x = 35;", 20_000)) + "\n```\nplain");
        var colorizer = new MarkdownColorizer(); colorizer.Attach(text);
        var refresh = typeof(MarkdownColorizer).GetMethod("EnsureKinds", BindingFlags.Instance | BindingFlags.NonPublic)!;
        refresh.Invoke(colorizer, [text]);
        text.Insert(text.GetLineByNumber(10_000).Offset + 4, "y"); refresh.Invoke(colorizer, [text]);
        Assert.InRange(colorizer.StateLinesVisited, 1, 2);
        text.Insert(text.GetLineByNumber(10_000).Offset, "/*"); refresh.Invoke(colorizer, [text]);
        Assert.True(colorizer.StateLinesVisited >= 10_000);
        text.Insert(text.GetLineByNumber(10_001).Offset, "*/"); refresh.Invoke(colorizer, [text]);
        Assert.True(colorizer.StateLinesVisited >= 9_000);
        text.Insert(text.GetLineByNumber(15_000).Offset, new string('x', 100_000)); refresh.Invoke(colorizer, [text]);
        Assert.InRange(colorizer.StateLinesVisited, 1, 2);
        colorizer.Attach(null);
    }

    [AvaloniaFact]
    public void Changing_an_offscreen_fence_language_refreshes_the_visible_body_without_scrolling()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("```csharp\n" + string.Join('\n', Enumerable.Repeat("var x = 35;", 200)) + "\n```\n");
        var editor = f.Window.EditorControl; Pump(f.Window);
        editor.CaretOffset = editor.Document.GetLineByNumber(100).Offset;
        editor.ScrollToLine(100); Pump(f.Window);
        var number = editor.TextArea.TextView.VisualLines.First().FirstDocumentLine.LineNumber;
        Assert.True(number > 1);
        Assert.True(editor.TextArea.TextView.VisualLines.First().Elements.Select(item => item.TextRunProperties.ForegroundBrush?.ToString()).Distinct().Count() >= 3);
        editor.Document.Replace(3, 6, "unknown"); Pump(f.Window);
        Assert.Single(editor.TextArea.TextView.VisualLines.Single(item => item.FirstDocumentLine.LineNumber == number)
            .Elements.Select(item => item.TextRunProperties.ForegroundBrush?.ToString()).Distinct());
    }

    private static string? Colour(AvaloniaEdit.TextEditor editor, int lineNumber, int column)
    {
        var line = editor.TextArea.TextView.VisualLines.Single(item => item.FirstDocumentLine.LineNumber == lineNumber);
        return line.Elements.Single(item => item.RelativeTextOffset <= column && item.RelativeTextOffset + item.DocumentLength > column)
            .TextRunProperties.ForegroundBrush?.ToString();
    }

    private static void Capture(Window window, string name)
    {
        var folder = Path.Combine(Screenshots.Folder, "reported-code-formatting"); Directory.CreateDirectory(folder);
        using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
        using var output = File.Create(Path.Combine(folder, name + ".png"));
        bitmap.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    }
}
