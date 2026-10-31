using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Views;
using Tern.Domain.Merging;

namespace Tern.Desktop.Tests;

public sealed class MergeLayoutTests
{
    [AvaloniaTheory]
    [InlineData(700, 500)]
    [InlineData(1000, 700)]
    public void Supported_dialog_sizes_keep_merge_result_clear_of_footer(double width, double height)
    {
        var merge = ThreeWayMerger.Merge("one\nbase\nthree\n", "one\nmine\nthree\n", "one\ndisk\nthree\n");
        var window = new MergeWindow("sample.md", merge) { Width = width, Height = height };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var result = Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), c => AutomationProperties.GetName(c) == "Merged result");
            var accept = Assert.Single(window.GetVisualDescendants().OfType<Button>(), c => AutomationProperties.GetName(c) == "Accept merged text");
            var resultRect = new Rect(result.TranslatePoint(default, window)!.Value, result.Bounds.Size);
            var acceptRect = new Rect(accept.TranslatePoint(default, window)!.Value, accept.Bounds.Size);
            Assert.True(resultRect.Bottom <= acceptRect.Top - 8, "The editable result overlaps the acceptance footer.");
            Assert.True(acceptRect.Bottom <= window.ClientSize.Height);
            Assert.True(resultRect.Height >= 160);
            foreach (var label in new[] { "Keep mine", "Take disk", "Both", "Original" })
            {
                var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), c => AutomationProperties.GetName(c) == label);
                var rect = new Rect(button.TranslatePoint(default, window)!.Value, button.Bounds.Size);
                Assert.True(rect.Right <= window.ClientSize.Width && rect.Left >= 0, $"Choice {label} is clipped horizontally.");
            }
        }
        finally { window.Close(); }
    }
}
