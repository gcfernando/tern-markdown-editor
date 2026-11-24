using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Shell;
using Tern.App.Commands;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class MotionWorkflowTests
{
    [AvaloniaTheory]
    [InlineData(Tern.App.Settings.ThemeChoice.Paper)]
    [InlineData(Tern.App.Settings.ThemeChoice.Midnight)]
    public async Task Rapid_banner_replacement_preserves_latest_errors_and_history_without_stale_exit_surfaces(Tern.App.Settings.ThemeChoice theme)
    {
        var reader = MotionPolicy.SystemAnimationsReader;
        MotionPolicy.SystemAnimationsReader = () => true;
        try
        {
            using var fixture = new DesktopFixture();
            fixture.Shell.Settings.Theme = theme;
            fixture.Window.ApplySettings();
            fixture.Shell.NewDocument("Notification motion does not edit this note.");
            Pump(fixture.Window);
            fixture.Shell.ShowBanner("Initial informational message", StatusSeverity.Info);
            Pump(fixture.Window);
            fixture.Shell.DismissBanner();
            fixture.Shell.ShowBanner("Latest persistent error", StatusSeverity.Error, new BannerAction("Retry", () => { }));
            Pump(fixture.Window);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            await Task.Delay(TimeSpan.FromMilliseconds(260));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            Pump(fixture.Window);
            var banner = fixture.Window.FindControl<Border>("NotificationBanner")!;
            var layer = fixture.Window.FindControl<Canvas>("ChromeMotionLayer")!;
            Assert.True(banner.IsVisible);
            Assert.Equal("Latest persistent error", fixture.Window.FindControl<TextBlock>("NotificationBannerText")!.Text);
            Assert.Empty(layer.Children);
            Assert.Contains(fixture.Shell.Activities.Items, item => item.Phase == "Initial informational message");
            Assert.Contains(fixture.Shell.Activities.Items, item => item.Phase == "Latest persistent error" && item.Severity == ActivitySeverity.Error);
            fixture.Shell.DismissBanner();
            Assert.False(banner.IsVisible);
            Assert.Single(layer.Children);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            await Task.Delay(TimeSpan.FromMilliseconds(260));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            Pump(fixture.Window);
            Assert.Empty(layer.Children);
            Assert.Contains(fixture.Shell.Activities.Items, item => item.Phase == "Latest persistent error");
            Assert.Equal("Notification motion does not edit this note.", fixture.Shell.ActiveTab!.CurrentText);
        }
        finally { MotionPolicy.SystemAnimationsReader = reader; MotionPolicy.Apply(false); }
    }

    [AvaloniaFact]
    public async Task Resuming_after_a_human_decision_restarts_loading_reveal_and_other_operations_remain_visible()
    {
        var reader = MotionPolicy.SystemAnimationsReader;
        MotionPolicy.SystemAnimationsReader = () => true;
        using var fixture = new DesktopFixture();
        using var first = fixture.Shell.Activities.Begin("First isolated operation");
        using var second = fixture.Shell.Activities.Begin("Second isolated operation");
        var firstLoading = new LoadingState(fixture.Shell.Activities, first.Id);
        var secondLoading = new LoadingState(fixture.Shell.Activities, second.Id);
        var window = new Window { Width = 480, Height = 300,
            Content = new StackPanel { Children = { firstLoading, secondLoading } } };
        try
        {
            window.Show();
            Pump(window);
            first.WaitForUser("Choose whether to proceed");
            await Task.Delay(MotionPolicy.LoadingRevealDelay + TimeSpan.FromMilliseconds(50));
            Pump(window);
            Assert.False(Within<ProgressBar>(firstLoading, "OperationProgress").IsVisible);
            Assert.True(Within<ProgressBar>(secondLoading, "OperationProgress").IsIndeterminate);
            first.Report("Processing the approved choice");
            Pump(window);
            Assert.False(Within<ProgressBar>(firstLoading, "OperationProgress").IsVisible);
            await Task.Delay(MotionPolicy.LoadingRevealDelay + TimeSpan.FromMilliseconds(50));
            Pump(window);
            Assert.True(Within<ProgressBar>(firstLoading, "OperationProgress").IsIndeterminate);
            first.Complete();
            Pump(window);
            Assert.False(firstLoading.IsVisible);
            Assert.False(Within<ProgressBar>(firstLoading, "OperationProgress").IsIndeterminate);
            Assert.True(secondLoading.IsVisible);
            Assert.True(Within<ProgressBar>(secondLoading, "OperationProgress").IsIndeterminate);
            second.Fail("Synthetic backend failure");
            Pump(window);
            Assert.False(secondLoading.IsVisible);
            Assert.False(Within<ProgressBar>(secondLoading, "OperationProgress").IsIndeterminate);
        }
        finally { window.Close(); MotionPolicy.SystemAnimationsReader = reader; MotionPolicy.Apply(false); }
    }

    [AvaloniaFact]
    public async Task Panel_exit_is_a_bounded_noninteractive_snapshot_while_real_state_and_focus_change_immediately()
    {
        var previousReader = MotionPolicy.SystemAnimationsReader;
        MotionPolicy.SystemAnimationsReader = () => true;
        try
        {
            using var fixture = new DesktopFixture();
            var tab = fixture.Shell.NewDocument("selection survives panel transitions");
            await fixture.Window.RunCommandAsync(CommandIds.ViewSource);
            Pump(fixture.Window);
            fixture.Window.EditorControl.Select(2, 8);
            var library = fixture.Window.FindControl<Border>("Sidebar")!;
            var layer = fixture.Window.FindControl<Canvas>("ChromeMotionLayer")!;
            Assert.True(library.GetVisualDescendants().OfType<Button>().First(button => button.IsEffectivelyVisible && button.IsEffectivelyEnabled).Focus());
            Assert.True(library.IsKeyboardFocusWithin);
            await fixture.Window.RunCommandAsync(CommandIds.ToggleSidebar);
            Assert.False(library.IsVisible);
            Assert.True(fixture.Window.EditorControl.IsKeyboardFocusWithin,
                $"Focus after collapse: {fixture.Window.FocusManager?.GetFocusedElement()?.GetType().Name}; Source visible: {fixture.Window.EditorControl.IsEffectivelyVisible}");
            Assert.Equal(tab.CurrentText.Substring(2, 8), fixture.Window.EditorControl.SelectedText);
            var snapshot = Assert.IsType<Image>(Assert.Single(layer.Children));
            Assert.False(snapshot.IsHitTestVisible);
            Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(snapshot));
            Assert.IsType<Avalonia.Media.Imaging.RenderTargetBitmap>(snapshot.Source);
            for (var index = 0; index < 12; index++)
            {
                await fixture.Window.RunCommandAsync(CommandIds.ToggleSidebar);
                Assert.True(library.IsVisible);
                Assert.Empty(layer.Children);
                await fixture.Window.RunCommandAsync(CommandIds.ToggleSidebar);
                Assert.False(library.IsVisible);
                Assert.InRange(layer.Children.Count, 0, 1);
            }
            await fixture.Window.RunCommandAsync(CommandIds.ToggleSidebar);
            Pump(fixture.Window);
            var inspector = fixture.Window.FindControl<Border>("RightPanel")!;
            var inspectorFocus = inspector.GetVisualDescendants().OfType<Control>()
                .First(control => control.Focusable && control.IsEffectivelyVisible && control.IsEffectivelyEnabled);
            Assert.True(inspectorFocus.Focus());
            await fixture.Window.RunCommandAsync(CommandIds.ToggleRightPanel);
            Assert.False(inspector.IsVisible);
            Assert.True(fixture.Window.EditorControl.TextArea.IsKeyboardFocusWithin);
            Assert.Single(layer.Children);
            fixture.Window.Width -= 24;
            Pump(fixture.Window);
            Assert.Empty(layer.Children);
            await fixture.Window.RunCommandAsync(CommandIds.ToggleRightPanel);
            Assert.True(inspector.IsVisible);
            await fixture.Window.RunCommandAsync(CommandIds.ToggleSidebar);
            fixture.Shell.Settings.ReduceMotion = true;
            fixture.Window.ApplySettings();
            Assert.Empty(layer.Children);
            await fixture.Window.RunCommandAsync(CommandIds.ToggleSidebar);
            Assert.True(library.IsVisible);
            await fixture.Window.RunCommandAsync(CommandIds.ToggleRightPanel);
            Assert.False(fixture.Window.FindControl<Border>("RightPanel")!.IsVisible);
            Assert.Empty(layer.Children);
            Assert.Equal(tab.CurrentText.Substring(2, 8), fixture.Window.EditorControl.SelectedText);
        }
        finally { MotionPolicy.SystemAnimationsReader = previousReader; MotionPolicy.Apply(false); }
    }

    [AvaloniaFact]
    public async Task Loading_reveal_uses_real_lifetime_and_reduced_motion_retains_text_and_measured_progress()
    {
        MotionPolicy.Apply(false, true);
        var feed = new ActivityFeed();
        using var operation = feed.Begin("Reading test files");
        var loading = new LoadingState(Assert.Single(feed.Items));
        var window = new Window { Width = 400, Height = 200, Content = loading };
        try
        {
            window.Show();
            Pump(window);
            var bar = Within<ProgressBar>(loading, "OperationProgress");
            Assert.False(bar.IsVisible);
            Assert.False(bar.IsIndeterminate);
            operation.Complete();
            loading.Update(feed.Items[0]);
            await Task.Delay(MotionPolicy.LoadingRevealDelay + TimeSpan.FromMilliseconds(50));
            Pump(window);
            Assert.False(bar.IsVisible);

            using var running = feed.Begin("Scanning test files");
            var item = feed.Items.Single(value => value.Id == running.Id) with { Started = DateTimeOffset.UtcNow.AddSeconds(-1) };
            loading.Update(item);
            Assert.True(bar.IsVisible);
            Assert.True(bar.IsIndeterminate);
            MotionPolicy.Apply(true, true);
            Assert.False(bar.IsVisible);
            Assert.Equal(item.Phase, Within<TextBlock>(loading, "OperationPhase").Text);
            loading.Update(item with { Completed = 2, Total = 4, UnitLabel = "files" });
            Assert.True(bar.IsVisible);
            Assert.False(bar.IsIndeterminate);
            Assert.Equal(.5, bar.Value);
            Assert.Equal("2 of 4 files", Within<TextBlock>(loading, "OperationUnits").Text);
            running.Cancel();
            loading.Update(feed.Items.Single(value => value.Id == running.Id));
            Assert.False(bar.IsVisible);
        }
        finally { window.Close(); MotionPolicy.Apply(false); }
    }

    [AvaloniaFact]
    public async Task Activity_progress_reuses_visible_controls_and_cancel_reaches_the_real_token_only_once()
    {
        using var fixture = new DesktopFixture();
        using var cancellation = new CancellationTokenSource();
        var cancellationCalls = 0;
        using var operation = fixture.Shell.Activities.Begin("Reading synthetic stream", cancel: () =>
        {
            cancellationCalls++;
            cancellation.Cancel();
        });
        var backend = Task.Delay(Timeout.Infinite, cancellation.Token);
        operation.Report("Reading actual entries", 1, 10, "entries");
        fixture.Window.FindControl<Button>("ActivityButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(fixture.Window);
        LoadingState FindLoading() => fixture.Window.GetVisualDescendants().OfType<LoadingState>()
            .Single(control => Within<TextBlock>(control, "OperationPhase").Text == "Reading actual entries");
        var original = FindLoading();
        await Task.Run(() =>
        {
            for (var index = 2; index <= 1000; index++) operation.Report("Reading actual entries", index, 2000, "entries");
        });
        Pump(fixture.Window);
        Assert.Same(original, FindLoading());
        var cancel = fixture.Window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel"));
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend);
        Assert.Equal(1, cancellationCalls);
        operation.Cancel("Canceled; the synthetic stream was not changed.");
        Pump(fixture.Window);
        Assert.DoesNotContain(fixture.Shell.Activities.Items, item => item.Id == operation.Id && item.IsActive);
        Assert.DoesNotContain(fixture.Window.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Cancel"));
        fixture.Window.HideOverlay();
    }

    [AvaloniaFact]
    public void Standard_control_transitions_follow_the_same_explicit_and_system_motion_policy()
    {
        using var fixture = new DesktopFixture();
        try
        {
            Pump(fixture.Window);
            MotionPolicy.Apply(false, true);
            Assert.False(MotionPolicy.IsReduced);
            var button = fixture.Window.FindControl<Button>("NewTabButton")!;
            var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().Single(value => value.Name == "PART_ContentPresenter");
            Assert.Contains(presenter.Transitions!, transition => transition is BrushTransition brush && brush.Duration == TimeSpan.FromMilliseconds(100));
            MotionPolicy.Apply(false, false);
            Assert.True(MotionPolicy.IsReduced);
            Assert.Empty(presenter.Transitions!);
            MotionPolicy.Apply(false, null);
            Assert.True(MotionPolicy.IsReduced);
            MotionPolicy.Apply(true, true);
            Assert.True(MotionPolicy.IsReduced);
            Assert.Equal(TimeSpan.Zero, MotionPolicy.Duration(MotionPurpose.Panel));
            Assert.NotNull(AutomationProperties.GetName(button));
        }
        finally { MotionPolicy.Apply(fixture.Shell.Settings.ReduceMotion); }
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static T Within<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
}
