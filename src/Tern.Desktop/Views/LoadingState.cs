using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Tern.App.Shell;
using Tern.Desktop.Services;

namespace Tern.Desktop.Views;

/// <summary>Presentation only: real Activity state owns all progress and cancellation.</summary>
internal sealed class LoadingState : UserControl
{
    private readonly TextBlock _phase = new() { Name = "OperationPhase", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _units = new() { Name = "OperationUnits", TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Name = "OperationProgress", Minimum = 0, Maximum = 1, Height = 4, IsVisible = false };
    private readonly TextBlock _elapsed = new() { Name = "OperationElapsed", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _reveal = new();
    private ActivityItem _item;
    private bool _attached;
    private readonly ActivityFeed? _feed;
    private readonly Guid _operationId;
    private readonly bool _hideWhenFinished;
    private readonly bool _forceReduced;
    private int _refreshQueued;

    public LoadingState(ActivityFeed feed, Guid operationId, bool hideWhenFinished = true, bool reduceMotion = false)
        : this(feed.Items.Single(item => item.Id == operationId))
    {
        _feed = feed;
        _operationId = operationId;
        _hideWhenFinished = hideWhenFinished;
        _forceReduced = reduceMotion;
        Refresh();
    }

    public LoadingState(ActivityItem item)
    {
        _item = item;
        Content = GuideParts.Vertical(_phase, _units, _elapsed, _progress);
        _reveal.Tick += (_, _) => { _reveal.Stop(); Refresh(); };
        _clock.Tick += (_, _) => ShowElapsed();
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            MotionPolicy.Changed += OnMotionChanged;
            if (_feed is not null) { _feed.Changed += OnFeedChanged; ReadOperation(); }
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            _reveal.Stop();
            _clock.Stop();
            MotionPolicy.Changed -= OnMotionChanged;
            if (_feed is not null) _feed.Changed -= OnFeedChanged;
        };
        Refresh();
    }

    public void Update(ActivityItem item)
    {
        _item = item;
        Refresh();
    }

    private void OnMotionChanged(object? sender, EventArgs e) => Refresh();

    private void OnFeedChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_attached) ReadOperation();
        }, DispatcherPriority.Background);
    }

    private void ReadOperation()
    {
        if (_feed?.Items.FirstOrDefault(item => item.Id == _operationId) is { } item) Update(item);
        else { _reveal.Stop(); IsVisible = false; }
    }

    private void Refresh()
    {
        _reveal.Stop();
        if (_hideWhenFinished) IsVisible = _item.IsActive;
        _phase.Text = _item.Phase;
        AutomationProperties.SetName(this, (_item.IsActive ? "Operation in progress: " : "") + _item.Phase);
        AutomationProperties.SetLiveSetting(_phase, _item.IsActive ? AutomationLiveSetting.Polite : AutomationLiveSetting.Off);
        _units.Text = _item.Completed is long count && count >= 0
            ? _item.Fraction is not null ? $"{count} of {_item.Total} {_item.UnitLabel}".TrimEnd()
                : $"{count} {_item.UnitLabel ?? "units"} processed"
            : "";
        _units.IsVisible = !string.IsNullOrEmpty(_units.Text);
        var remaining = MotionPolicy.LoadingRevealDelay - (DateTimeOffset.UtcNow - (_item.RunningSince ?? _item.Started));
        var canShow = _item.State == ActivityState.Active
            && !((MotionPolicy.ProgressMotionOff || _forceReduced) && _item.Fraction is null);
        // With Reduce motion on, a counting line shows the work is still alive without movement.
        if (_attached && _item.State == ActivityState.Active && _item.Fraction is null && !canShow)
        {
            ShowElapsed();
            _clock.Start();
        }
        else
        {
            _clock.Stop();
            _elapsed.IsVisible = false;
        }
        var show = canShow && remaining <= TimeSpan.Zero;
        _progress.IsIndeterminate = show && _item.IsIndeterminate;
        _progress.Value = _item.Fraction ?? 0;
        _progress.IsVisible = show;
        if (canShow && remaining > TimeSpan.Zero && _attached)
        {
            _reveal.Interval = remaining;
            _reveal.Start();
        }
    }

    private void ShowElapsed()
    {
        var seconds = (int)(DateTimeOffset.UtcNow - (_item.RunningSince ?? _item.Started)).TotalSeconds;
        _elapsed.Text = $"Still working · {seconds} s";
        _elapsed.IsVisible = seconds >= 1;
    }
}
