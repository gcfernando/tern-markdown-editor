using Avalonia.LogicalTree;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed partial class AIWritingWorkflowTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Knowledge_busy_request_uses_shared_progress_and_disables_reset_navigation_until_cancelled(bool reduced)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = Fixture(async (_, token) =>
        {
            started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Reply("never");
        });
        var previousReader = MotionPolicy.SystemAnimationsReader;
        try
        {
            MotionPolicy.SystemAnimationsReader = () => true;
            var path = await f.WriteAsync("notes/launch.md", "# Launch\nLaunch facts.");
            Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
            var tab = f.Shell.NewDocument("Unchanged draft");
            f.Shell.Settings.ReduceMotion = reduced; f.Shell.Settings.KnowledgeAllowRemote = true;
            f.Window.ApplySettings(); f.Window.ShowKnowledgeAssistant();
            var window = Assert.Single(f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>()); Pump(window);
            var panel = window.Assistant;
            Find<TextBox>(panel, "Your question").Text = "Launch";
            Click(Find<Button>(panel, "Find my notes"));
            await Until(() => Find<Button>(panel, "Get my answer").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
            
            
            Click(Find<Button>(panel, "Get my answer")); await Until(() => started.Task.IsCompleted);
            Assert.False(Find<Button>(panel, "Ask a new question").IsEnabled);
            Assert.All(panel.GetVisualDescendants().OfType<Button>().Where(button =>
                AutomationProperties.GetName(button)?.StartsWith("Knowledge navigation", StringComparison.Ordinal) == true), button => Assert.False(button.IsEnabled));
            Assert.Single(panel.GetVisualDescendants().OfType<LoadingState>());
            await Task.Delay(MotionPolicy.LoadingRevealDelay + TimeSpan.FromMilliseconds(80)); Pump(window);
            var progress = panel.GetVisualDescendants().OfType<ProgressBar>().Single();
            Assert.Equal(!reduced, progress.IsVisible);
            Assert.Equal(!reduced, progress.IsIndeterminate);
            await CaptureKnowledgeWindow(window, $"redesign-busy-reduced{reduced}");
            Click(Find<Button>(panel, "Cancel"));
            await Until(() => !Find<Button>(panel, "Cancel").IsEnabled);
            Assert.Empty(panel.GetVisualDescendants().OfType<LoadingState>());
            Assert.True(Find<Button>(panel, "Ask a new question").IsEnabled);
            Assert.StartsWith("Cancelled", Find<TextBlock>(panel, "Explore status").Text);
            Assert.Equal("Unchanged draft", tab.CurrentText);
        }
        finally { MotionPolicy.SystemAnimationsReader = previousReader; }
    }
}
