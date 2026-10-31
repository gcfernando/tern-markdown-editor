using System.Diagnostics;
using System.Text;
using Avalonia.Headless.XUnit;
using Xunit.Sdk;

namespace Tern.PreviewView.Tests;

public class PerformanceTests
{
    private static string Generate(int lines)
    {
        var text = new StringBuilder();
        var line = 0;
        var block = 0;
        while (line < lines)
        {
            switch (block++ % 6)
            {
                case 0:
                    text.Append("## Section ").Append(block).Append("\n\n");
                    line += 2;
                    break;
                case 1:
                case 2:
                    text.Append("Paragraph ").Append(block).Append(" has **bold**, *italic*, `code` and a [link](https://example.com/").Append(block).Append(") with enough words to wrap across lines in the reading column.\n\n");
                    line += 2;
                    break;
                case 3:
                    text.Append("- item a\n- item b\n- item c\n\n");
                    line += 4;
                    break;
                case 4:
                    text.Append("```csharp\nvar x = ").Append(block).Append(";\nConsole.WriteLine(x);\n```\n\n");
                    line += 5;
                    break;
                default:
                    text.Append("| A | B |\n|---|---|\n| 1 | 2 |\n\n");
                    line += 4;
                    break;
            }
        }

        return text.ToString();
    }

    [AvaloniaFact]
    public void FiveThousandLines_RenderAndReRenderQuickly()
    {
        var source = Generate(5000);
        var sw = Stopwatch.StartNew();
        var first = Harness.Build(source);
        var buildMs = sw.Elapsed.TotalMilliseconds;

        var view = new PreviewView();
        var window = new Avalonia.Controls.Window { Width = 900, Height = 700, Content = view };
        window.Show();
        sw.Restart();
        view.Document = first;
        Harness.Pump(window);
        var initialMs = sw.Elapsed.TotalMilliseconds;
        var realised = view.Panel.RealizedCount;

        view.ScrollToSourceLine(2500);
        Harness.Pump(window, 6);

        var edited = Harness.Build(source.Replace("Paragraph 2 ", "Paragraph two ", StringComparison.Ordinal));
        sw.Restart();
        view.Document = edited;
        Harness.Pump(window);
        var editMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        view.Scroller.Offset = new Avalonia.Vector(0, view.Scroller.Offset.Y + 3000);
        Harness.Pump(window);
        var scrollMs = sw.Elapsed.TotalMilliseconds;

        var message = FormattableString.Invariant(
            $"PERF blocks={first.Blocks.Count} lines={first.LineCount} model-build={buildMs:F0}ms initial-render={initialMs:F0}ms edit-rerender={editMs:F0}ms scroll-3000px={scrollMs:F0}ms realised-blocks={realised}");
        Console.WriteLine(message);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "tern-preview-perf.txt"), message);

        Assert.True(realised < 200, $"only the viewport should be realised ({realised})");
        Assert.True(initialMs < 2000, message);
        Assert.True(editMs < 1000, message);
        window.Close();
    }
}
