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
    private readonly StackPanel _rows = new() { Spacing = 12 };
    private readonly TextBlock _pageLabel = new();
    private readonly Button _older = new() { Content = "Older activity" };
    private readonly Button _newer = new() { Content = "Newer activity" };
    private int _page;
    public ActivityPresenter(ActivityFeed feed)
    {
        _feed = feed;
        _older.Click += (_, _) => { _page++; Refresh(); };
        _newer.Click += (_, _) => { _page = Math.Max(0, _page - 1); Refresh(); };
        Content = GuideParts.Vertical(GuideParts.Actions(_newer, _older), _pageLabel, new ScrollViewer { Content = _rows, MaxHeight = 420 });
        AttachedToVisualTree += (_, _) => { _feed.Changed += Changed; Refresh(); };
        DetachedFromVisualTree += (_, _) => _feed.Changed -= Changed;
        Refresh();
    }
    private void Changed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);
    private void Refresh()
    {
        _rows.Children.Clear();
        var items = _feed.Items;
        _page = Math.Min(_page, Math.Max(0, (items.Count - 1) / 50));
        _older.IsEnabled = (_page + 1) * 50 < items.Count; _newer.IsEnabled = _page > 0;
        _pageLabel.Text = items.Count == 0 ? "" : $"{items.Count} session items · showing {_page * 50 + 1}–{Math.Min(items.Count, (_page + 1) * 50)}";
        if (items.Count == 0) _rows.Children.Add(new TextBlock { Text = "No activity in this session.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        foreach (var item in items.Reverse().Skip(_page * 50).Take(50))
        {
            var row = new StackPanel { Spacing = 6 };
            var label = new TextBlock { Text = item.Phase, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            AutomationProperties.SetLiveSetting(label, AutomationLiveSetting.Polite);
            label[!TextBlock.ForegroundProperty] = this.GetResourceObservable(item.Severity switch
            { ActivitySeverity.Error => "Tern.Danger", ActivitySeverity.Warning => "Tern.Warning", ActivitySeverity.Success => "Tern.Success", _ => "Tern.Text" }).ToBinding();
            row.Children.Add(label);
            if (item.Identity is { } identity) row.Children.Add(new TextBlock { Text = identity, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            if (item.Completed is { } count) row.Children.Add(new TextBlock { Text = item.Total is > 0 ? $"{count} of {item.Total}" : $"{count} processed" });
            if (item.Fraction is { } fraction) row.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = fraction, IsIndeterminate = false });
            if (item.Details is { Length: > 0 } details)
                row.Children.Add(new TextBox { Text = details, IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxHeight = 180, AcceptsReturn = true });
            if (item.Cancel is { } cancel)
            {
                var button = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Left };
                button.Click += (_, _) =>
                {
                    button.IsEnabled = false;
                    if (!_feed.Items.Any(current => current.Id == item.Id && current.State == ActivityState.Active)) return;
                    try { cancel(); } catch (ObjectDisposedException) { label.Text = "This operation has already ended."; }
                };
                row.Children.Add(button);
            }
            if (item.State != ActivityState.Active)
            {
                var dismiss = new Button { Content = "Dismiss", HorizontalAlignment = HorizontalAlignment.Left };
                AutomationProperties.SetName(dismiss, "Dismiss " + item.Kind);
                dismiss.Click += (_, _) => _feed.Dismiss(item.Id);
                row.Children.Add(dismiss);
            }
            _rows.Children.Add(row);
        }
    }
}
