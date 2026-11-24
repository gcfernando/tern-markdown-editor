using Tern.App.Shell;

namespace Tern.App.Tests;

/// <summary>SB-01 / U-01: severity-aware status messages mirrored into Activity.</summary>
public sealed class StatusNotificationTests
{
    [Fact]
    public void Display_time_is_six_seconds_plus_sixty_ms_per_character()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(6000 + 60 * 80), ShellViewModel.MinimumDisplayTime(new string('x', 80)));
        Assert.True(ShellViewModel.MinimumDisplayTime("Saved") >= TimeSpan.FromSeconds(6));
    }

    [Theory]
    [InlineData(StatusSeverity.Info, ActivitySeverity.Neutral)]
    [InlineData(StatusSeverity.Success, ActivitySeverity.Success)]
    [InlineData(StatusSeverity.Warning, ActivitySeverity.Warning)]
    [InlineData(StatusSeverity.Error, ActivitySeverity.Error)]
    public void Every_status_message_is_mirrored_into_activity_with_its_severity(StatusSeverity severity, ActivitySeverity expected)
    {
        using var f = new ShellFixture();
        f.Shell.SetStatus("Something happened with " + severity, severity);

        Assert.Equal(severity, f.Shell.StatusSeverity);
        var item = Assert.Single(f.Shell.Activities.Items, i => i.Kind == "Status");
        Assert.Equal("Something happened with " + severity, item.Phase);
        Assert.Equal(expected, item.Severity);
    }

    [Fact]
    public void Errors_and_warnings_do_not_expire()
    {
        using var f = new ShellFixture();
        f.Shell.SetStatus("Link repaired in the editor. Save to update the file; Undo to revert.", StatusSeverity.Warning);
        Assert.Equal("Link repaired in the editor. Save to update the file; Undo to revert.", f.Shell.StatusMessage);
        f.Shell.SetStatus("Disk is full", StatusSeverity.Error);
        Assert.Equal("Disk is full", f.Shell.StatusMessage);
        Assert.Equal(StatusSeverity.Error, f.Shell.StatusSeverity);
    }

    [Fact]
    public void Repeating_the_same_message_is_logged_once()
    {
        using var f = new ShellFixture();
        f.Shell.SetStatus("Index refreshed", StatusSeverity.Info);
        f.Shell.SetStatus("Index refreshed", StatusSeverity.Info);
        Assert.Single(f.Shell.Activities.Items, i => i.Kind == "Status");
    }

    [Fact]
    public async Task Status_posted_from_a_background_thread_is_recorded_without_loss()
    {
        using var f = new ShellFixture();
        var dispatcher = new QueueDispatcher();
        f.Shell.UiDispatcher = dispatcher;
        await Task.Run(() => f.Shell.SetStatus("From background", StatusSeverity.Info));

        Assert.Empty(f.Shell.Activities.Items); // not applied on the worker thread
        Assert.Equal(1, dispatcher.Pending);
        dispatcher.Drain();
        Assert.Contains(f.Shell.Activities.Items, i => i.Phase == "From background");
        Assert.Equal("From background", f.Shell.StatusMessage);
    }

    [Fact]
    public async Task Concurrent_background_posts_are_all_recorded()
    {
        using var f = new ShellFixture();
        var dispatcher = new QueueDispatcher();
        f.Shell.UiDispatcher = dispatcher;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(n => Task.Run(() => f.Shell.SetStatus("Message " + n, StatusSeverity.Warning))));
        dispatcher.Drain();
        Assert.Equal(20, f.Shell.Activities.Items.Count(i => i.Kind == "Status"));
    }

    [Fact]
    public async Task Info_message_is_held_while_the_status_area_has_pointer_or_focus()
    {
        using var f = new ShellFixture();
        const string message = "Short note";
        f.Shell.IsStatusHeld = true;
        f.Shell.SetStatus(message, StatusSeverity.Info);
        await Task.Delay(ShellViewModel.MinimumDisplayTime(message) + TimeSpan.FromMilliseconds(1200));
        Assert.Equal(message, f.Shell.StatusMessage);
        f.Shell.IsStatusHeld = false;
        await Task.Delay(1500);
        Assert.Equal("Ready", f.Shell.StatusMessage);
    }

    [Fact]
    public async Task Status_posted_from_a_background_thread_without_host_dispatcher_is_recorded()
    {
        using var f = new ShellFixture();
        await Task.Run(() => f.Shell.SetStatus("From background", StatusSeverity.Info));
        await Task.Delay(100);
        Assert.Contains(f.Shell.Activities.Items, i => i.Phase == "From background");
    }

    [Fact]
    public void Banner_shows_actions_stays_until_dismissed_and_is_logged()
    {
        using var f = new ShellFixture();
        var retried = 0;
        f.Shell.ShowBanner("Indexing failed.", StatusSeverity.Error, new BannerAction("Rebuild", () => retried++));

        var banner = Assert.IsType<BannerState>(f.Shell.Banner);
        Assert.Equal(StatusSeverity.Error, banner.Severity);
        Assert.Equal("Rebuild", Assert.Single(banner.Actions).Label);
        banner.Actions[0].Invoke();
        Assert.Equal(1, retried);
        Assert.Contains(f.Shell.Activities.Items, i => i.Phase == "Indexing failed." && i.Severity == ActivitySeverity.Error);

        f.Shell.DismissBanner();
        Assert.Null(f.Shell.Banner);
    }

    [Fact]
    public void Banner_posted_from_a_background_thread_goes_through_the_dispatcher()
    {
        using var f = new ShellFixture();
        var dispatcher = new QueueDispatcher();
        f.Shell.UiDispatcher = dispatcher;
        Task.Run(() => f.Shell.ShowBanner("Background problem", StatusSeverity.Warning)).Wait();
        Assert.Null(f.Shell.Banner);
        dispatcher.Drain();
        Assert.Equal("Background problem", f.Shell.Banner?.Text);
    }

    [Fact]
    public async Task Warning_stays_visible_past_the_minimum_display_time()
    {
        using var f = new ShellFixture();
        const string message = "Historical version opened as an unsaved copy.";
        f.Shell.SetStatus(message, StatusSeverity.Warning);
        await Task.Delay(ShellViewModel.MinimumDisplayTime(message) + TimeSpan.FromMilliseconds(1500));
        Assert.Equal(message, f.Shell.StatusMessage);
    }

    private sealed class QueueDispatcher : IUiDispatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();
        [ThreadStatic] private static bool _drainingThread;
        public int Pending => _queue.Count;
        public bool CheckAccess() => _drainingThread;
        public void Post(Action action) => _queue.Enqueue(action);
        public void Drain()
        {
            _drainingThread = true;
            try { while (_queue.TryDequeue(out var a)) a(); }
            finally { _drainingThread = false; }
        }
    }

    [Fact]
    public void Direct_assignment_of_a_failure_message_is_classified_as_error_and_logged()
    {
        using var f = new ShellFixture();
        f.Shell.StatusMessage = "Could not save the file";
        Assert.Equal(StatusSeverity.Error, f.Shell.StatusSeverity);
        Assert.Contains(f.Shell.Activities.Items, i => i.Severity == ActivitySeverity.Error && i.Phase == "Could not save the file");
    }
}
