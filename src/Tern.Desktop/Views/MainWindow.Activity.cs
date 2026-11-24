using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private int _activityRefreshPending;
    private void OnActivityClick(object? sender, RoutedEventArgs e)
    {
        var close = new Button { Content = "Close activity" };
        close.Click += (_, _) => HideOverlay();
        var panel = new StackPanel { [!StackPanel.SpacingProperty] = GuideParts.Space(12).ToBinding(), Margin = new Thickness(20), Children =
        { new TextBlock { Text = "Activity · this session", FontWeight = Avalonia.Media.FontWeight.SemiBold }, new ActivityPresenter(_shell.Activities), close } };
        var card = new Border { MaxWidth = 640, MaxHeight = Math.Max(160, Bounds.Height - 160), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, Classes = { "card" } };
        ShowOverlay(card);
        close.Focus();
    }

    private void OnActivityChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _activityRefreshPending, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _activityRefreshPending, 0);
            if (_windowClosed) return;
            var items = _shell.Activities.Items;
            var active = items.Count(i => i.IsActive);
            var failures = items.Count(i => i.State == Tern.App.Shell.ActivityState.Failed);
            ActivityButton.Content = active > 0 ? $"Working ({active})" : failures > 0 ? $"Activity · {failures} failed" : "Activity";
        }, DispatcherPriority.Background);
    }

    private void ApplyStatusSeverity()
    {
        var severity = _shell.StatusSeverity;
        AutomationProperties.SetLiveSetting(StatusMessage, severity == Tern.App.Shell.StatusSeverity.Error
            ? Avalonia.Automation.AutomationLiveSetting.Assertive : Avalonia.Automation.AutomationLiveSetting.Polite);
        var key = severity switch
        {
            Tern.App.Shell.StatusSeverity.Error => "Tern.Danger",
            Tern.App.Shell.StatusSeverity.Warning => "Tern.Warning",
            Tern.App.Shell.StatusSeverity.Success => "Tern.Success",
            _ when _shell.StatusMessage is "Ready" or "" => null,
            _ => "Tern.Info",
        };
        StatusGlyph.IsVisible = key is not null;
        StatusGlyph.Text = severity switch
        {
            Tern.App.Shell.StatusSeverity.Error => "×",
            Tern.App.Shell.StatusSeverity.Warning => "!",
            Tern.App.Shell.StatusSeverity.Success => "✓",
            _ => "i",
        };
        if (key is not null)
        {
            StatusMessage[!TextBlock.ForegroundProperty] = this.GetResourceObservable(key).ToBinding();
            StatusGlyph[!TextBlock.ForegroundProperty] = this.GetResourceObservable(key).ToBinding();
        }
        else StatusMessage.ClearValue(TextBlock.ForegroundProperty);
    }

    private void RefreshNotificationBanner()
    {
        if (_windowClosed) return;
        var banner = _shell.Banner;
        _bannerMotion!.SetVisible(banner is not null);
        NotificationBannerActions.Children.Clear();
        if (banner is null) return;
        NotificationBannerText.Text = banner.Text;
        AutomationProperties.SetLiveSetting(NotificationBannerText, banner.Severity == Tern.App.Shell.StatusSeverity.Error
            ? Avalonia.Automation.AutomationLiveSetting.Assertive : Avalonia.Automation.AutomationLiveSetting.Polite);
        NotificationBanner[!Border.BackgroundProperty] = this.GetResourceObservable(
            banner.Severity switch
            {
                Tern.App.Shell.StatusSeverity.Error => "Tern.ErrorBackground",
                Tern.App.Shell.StatusSeverity.Warning => "Tern.WarningBackground",
                Tern.App.Shell.StatusSeverity.Success => "Tern.SuccessBackground",
                _ => "Tern.InfoBackground",
            }).ToBinding();
        foreach (var action in banner.Actions)
        {
            var button = new Button { Content = action.Label };
            button.Click += (_, _) => action.Invoke();
            NotificationBannerActions.Children.Add(button);
        }
    }

    private void OnDismissBannerClick(object? sender, RoutedEventArgs e) => _shell.DismissBanner();

    private void WireStatusHold()
    {
        void Hold(bool value) => _shell.IsStatusHeld = value;
        StatusMessageButton.PointerEntered += (_, _) => Hold(true);
        StatusMessageButton.PointerExited += (_, _) => Hold(StatusMessageButton.IsKeyboardFocusWithin);
        StatusMessageButton.GotFocus += (_, _) => Hold(true);
        StatusMessageButton.LostFocus += (_, _) => Hold(StatusMessageButton.IsPointerOver);
    }
}
