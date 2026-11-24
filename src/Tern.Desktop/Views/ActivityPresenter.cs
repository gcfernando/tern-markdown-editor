using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Tern.App.Shell;

namespace Tern.Desktop.Views;

/// <summary>Reusable view of session operations; never owns or restarts the underlying work.</summary>
internal sealed class ActivityPresenter : UserControl
{
    private readonly ActivityFeed _feed;
    private readonly StackPanel _rows = new() { [!StackPanel.SpacingProperty] = GuideParts.Space(12).ToBinding() };
    private readonly TextBlock _pageLabel = new();
    private readonly Button _older = new() { Content = "Older activity" };
    private readonly Button _newer = new() { Content = "Newer activity" };
    private int _page;
    private int _refreshQueued;
    private bool _attached;
    private readonly Dictionary<Guid, (ActivityItem Item, StackPanel Row, LoadingState Loading)> _cache = [];
    private readonly HashSet<Guid> _cancelRequested = [];
    public ActivityPresenter(ActivityFeed feed)
    {
        _feed = feed;
        _older.Click += (_, _) => { _page++; Refresh(); };
        _newer.Click += (_, _) => { _page = Math.Max(0, _page - 1); Refresh(); };
        Content = GuideParts.Vertical(GuideParts.ButtonRow(_newer, _older), _pageLabel, new ScrollViewer { Content = _rows, MaxHeight = 420 });
        AttachedToVisualTree += (_, _) => { _attached = true; _feed.Changed += Changed; Refresh(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; _feed.Changed -= Changed; };
        Refresh();
    }
    private void Changed(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_attached) Refresh();
        }, DispatcherPriority.Background);
    }
    private void Refresh()
    {
        var items = _feed.Items;
        _page = Math.Min(_page, Math.Max(0, (items.Count - 1) / 50));
        _older.IsEnabled = (_page + 1) * 50 < items.Count; _newer.IsEnabled = _page > 0;
        _pageLabel.Text = items.Count == 0 ? "" : $"{items.Count} session items · showing {_page * 50 + 1}–{Math.Min(items.Count, (_page + 1) * 50)}";
        var visible = items.Reverse().Skip(_page * 50).Take(50).ToArray();
        var desired = visible.Select(RowFor).ToArray();
        foreach (var id in _cache.Keys.Where(id => !visible.Any(item => item.Id == id)).ToArray()) _cache.Remove(id);
        _cancelRequested.RemoveWhere(id => !items.Any(item => item.Id == id && item.IsActive));
        if (items.Count == 0)
        {
            if (_rows.Children.Count != 1 || _rows.Children[0] is not TextBlock)
            {
                _rows.Children.Clear();
                _rows.Children.Add(new TextBlock { Text = "No activity in this session.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            }
            return;
        }
        for (var index = 0; index < desired.Length; index++)
        {
            if (index < _rows.Children.Count && ReferenceEquals(_rows.Children[index], desired[index])) continue;
            _rows.Children.Remove(desired[index]);
            _rows.Children.Insert(index, desired[index]);
        }
        while (_rows.Children.Count > desired.Length) _rows.Children.RemoveAt(_rows.Children.Count - 1);
    }

    private StackPanel RowFor(ActivityItem item)
    {
            if (_cache.TryGetValue(item.Id, out var cached) &&
                cached.Item with { Phase = item.Phase, Completed = item.Completed, Total = item.Total, UnitLabel = item.UnitLabel } == item)
            {
                if (cached.Item != item) cached.Loading.Update(item);
                _cache[item.Id] = (item, cached.Row, cached.Loading);
                return cached.Row;
            }
            var row = new StackPanel { [!StackPanel.SpacingProperty] = GuideParts.Space(4).ToBinding() };
            var label = new LoadingState(item);
            label[!UserControl.ForegroundProperty] = this.GetResourceObservable(item.Severity switch
            { ActivitySeverity.Error => "Tern.Danger", ActivitySeverity.Warning => "Tern.Warning", ActivitySeverity.Success => "Tern.Success", _ => "Tern.Info" }).ToBinding();
            row.Children.Add(label);
            if (item.Identity is { } identity) row.Children.Add(new TextBlock { Text = identity, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            if (item.Details is { Length: > 0 } details)
                row.Children.Add(new TextBox { Text = details, IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxHeight = 180, AcceptsReturn = true });
            foreach (var action in item.Actions)
            {
                var button = new Button { Content = action.Label, HorizontalAlignment = HorizontalAlignment.Left };
                button.Click += (_, _) =>
                {
                    if (_feed.Items.Any(current => current.Id == item.Id)) action.Invoke();
                };
                row.Children.Add(button);
            }
            if (item.CanCancel && item.Cancel is { } cancel)
            {
                var button = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = !_cancelRequested.Contains(item.Id) };
                button.Click += (_, _) =>
                {
                    if (!_cancelRequested.Add(item.Id)) return;
                    button.IsEnabled = false;
                    if (!_feed.Items.Any(current => current.Id == item.Id && current.CanCancel)) return;
                    try { cancel(); }
                    catch (ObjectDisposedException) { label.Update(item with { State = ActivityState.Completed, Phase = "This operation has already ended." }); }
                };
                row.Children.Add(button);
            }
            if (!item.IsActive)
            {
                var dismiss = new Button { Content = "Dismiss", HorizontalAlignment = HorizontalAlignment.Left };
                AutomationProperties.SetName(dismiss, "Dismiss " + item.Kind);
                dismiss.Click += (_, _) => _feed.Dismiss(item.Id);
                row.Children.Add(dismiss);
            }
            _cache[item.Id] = (item, row, label);
            return row;
    }
}
