using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private void OnActivityClick(object? sender, RoutedEventArgs e)
    {
        var close = new Button { Content = "Close activity" };
        close.Click += (_, _) => HideOverlay();
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(20), Children =
        { new TextBlock { Text = "Activity · this session", FontWeight = Avalonia.Media.FontWeight.SemiBold }, new ActivityPresenter(_shell.Activities), close } };
        var card = new Border { MaxWidth = 640, MaxHeight = Math.Max(160, Bounds.Height - 160), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, Classes = { "card" } };
        ShowOverlay(card);
        close.Focus();
    }

    private void OnActivityChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_windowClosed) return;
        var items = _shell.Activities.Items;
        var active = items.Count(i => i.State == Tern.App.Shell.ActivityState.Active);
        var failures = items.Count(i => i.State == Tern.App.Shell.ActivityState.Failed);
        ActivityButton.Content = active > 0 ? $"Working ({active})" : failures > 0 ? $"Activity · {failures} failed" : "Activity";
    });
}
