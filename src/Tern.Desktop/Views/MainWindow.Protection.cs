using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tern.App.Documents;
using Tern.App.Shell;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private void UpdateProtection(DocumentTab? tab)
    {
        DocumentProblem.IsVisible = false;
        if (tab is null) return;
        var state = DocumentProtection.For(tab, _shell.ActiveDocumentState);
        StatusSaved.Text = state.Label;
        StatusSaved[!TextBlock.ForegroundProperty] = this.GetResourceObservable(state.Severity switch
        { ActivitySeverity.Error => "Tern.Danger", ActivitySeverity.Warning => "Tern.Warning", ActivitySeverity.Success => "Tern.Success", _ => "Tern.Muted" }).ToBinding();
        ToolTip.SetTip(StatusSaved, state.Details);
        DocumentProblem.IsVisible = state.Severity is ActivitySeverity.Error or ActivitySeverity.Warning;
        DocumentProblemText.Text = state.Label + ". Your open text is kept.";
    }

    private void OnProtectionClick(object? sender, RoutedEventArgs e)
    {
        if (_shown is not { } tab) return;
        var state = DocumentProtection.For(tab, _shell.ActiveDocumentState);
        var details = new TextBox { Text = state.Details + $"\n{tab.Analysis?.WordCount ?? 0:N0} words · Markdown profile: {tab.Profile}" + (tab.SourceUrl is null ? "" : "\n" + (tab.IsCachedSource ? "Cached copy. " : "") + "Edits never update the source website.\n" + string.Join("\n", tab.SourceWarnings)), IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxHeight = 260 };
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => HideOverlay();
        var retry = new Button { Content = "Retry save", IsEnabled = !tab.IsReadOnly && !_shell.IsBusy };
        var copy = new Button { Content = "Save as…", IsEnabled = !_shell.IsBusy };
        var recovery = new Button { Content = "Retry recovery checkpoint", IsVisible = tab.RecoveryError is not null, IsEnabled = !_shell.IsBusy };
        recovery.Click += async (_, _) => { if (!_shell.Tabs.Contains(tab) || _shell.IsBusy) return; recovery.IsEnabled = false; await _shell.RetryRecoveryAsync(tab); if (!_windowClosed) HideOverlay(); };
        retry.Click += async (_, _) => { if (!_shell.Tabs.Contains(tab) || _shell.IsBusy) return; retry.IsEnabled = false; await _shell.SaveTabAsync(tab); HideOverlay(); };
        copy.Click += async (_, _) => { if (!_shell.Tabs.Contains(tab) || _shell.IsBusy) return; copy.IsEnabled = false; await _shell.SaveTabAsAsync(tab); HideOverlay(); };
        var latest = new Button { Content = "Fetch latest from URL…", IsVisible = tab.SourceUrl is not null };
        var profile = new Button { Content = "Change Markdown profile…" };
        profile.Click += (_, args) => { if (!ReferenceEquals(tab, _shown)) return; HideOverlay(); OnProfileClick(profile, args); };
        latest.Click += async (_, _) =>
        {
            if (!_shell.Tabs.Contains(tab) || tab.SourceUrl is not { } url) return;
            var revision = tab.Session.Revision;
            HideOverlay();
            var window = new OpenUrlWindow(new Services.MarkdownUrlLoader(), url, cacheRoot: Services.MarkdownUrlCache.Root(_shell.Paths.StateDirectory), activities: _shell.Activities);
            if (await window.ShowDialog<Services.MarkdownUrlDocument?>(this) is not { } result) return;
            if (!_shell.Tabs.Contains(tab) || tab.SourceUrl != url || !tab.Session.IsCurrent(revision))
            { _shell.StatusMessage = "The source note changed during refresh. Your edits were kept; reopen Fetch latest to review a new copy."; return; }
            if (window.DownloadRequested) await DownloadUrlDocumentAsync(result);
            else await OpenUrlDocumentAsync(result, DuplicateOpenBehavior.Ask);
        };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children =
        { new TextBlock { Text = tab.DisplayName + " · " + state.Label, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, details,
          GuideParts.Actions(retry, recovery, copy, latest, profile, close) } };
        ShowOverlay(new Border { MaxWidth = 640, MaxHeight = Math.Max(160, Bounds.Height - 100), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Classes = { "card" }, Child = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } });
        close.Focus();
    }
}
