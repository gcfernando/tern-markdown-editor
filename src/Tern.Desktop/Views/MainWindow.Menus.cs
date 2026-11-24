using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tern.App.Commands;
using Tern.App.Settings;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private void RefreshMenuState()
    {
        if (ApplicationMenu is null) return;
        foreach (var item in ApplicationMenu.Items.OfType<MenuItem>().SelectMany(MenuDescendants))
        {
            bool? state = item.Tag switch
            {
                CommandIds.ToggleSidebar => Sidebar.IsVisible,
                CommandIds.ToggleRightPanel => RightPanel.IsVisible,
                CommandIds.ToggleReference => ReferencePane.IsVisible,
                CommandIds.ToggleFocus => _focusMode,
                "System" => _shell.Settings.Theme == ThemeChoice.System,
                "Paper" => _shell.Settings.Theme == ThemeChoice.Paper,
                "Midnight" => _shell.Settings.Theme == ThemeChoice.Midnight,
                "Graphite" => _shell.Settings.Theme == ThemeChoice.Graphite,
                "Harbor" => _shell.Settings.Theme == ThemeChoice.Harbor,
                "Linen" => _shell.Settings.Theme == ThemeChoice.Linen,
                "Mist" => _shell.Settings.Theme == ThemeChoice.Mist,
                _ => null
            };
            if (state is { } check) item.IsChecked = check;
        }
    }

    private static IEnumerable<MenuItem> MenuDescendants(MenuItem root)
    {
        yield return root;
        foreach (var child in root.Items.OfType<MenuItem>().SelectMany(MenuDescendants)) yield return child;
    }

    private void OnThemeMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } || !Enum.TryParse<ThemeChoice>(value, out var theme)) return;
        e.Handled = true;
        _shell.Settings.Theme = theme;
        ApplyTheme();
        RebuildPreview();
        _shell.SaveSettings();
    }

    private void OnOpenRecentSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.Source, OpenRecentMenu)) RefreshRecentMenu();
    }

    private async void OnRecentMoreClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await RunCommandAsync(CommandIds.LibraryRecent);
    }

    internal void RefreshRecentMenu()
    {
        OpenRecentMenu.Items.Clear();
        foreach (var kind in new[] { RecentKind.File, RecentKind.Workspace, RecentKind.Url })
        {
            var entries = _shell.Recent.Get(kind).OrderByDescending(entry => entry.LastOpenedUtc)
                .Take(kind == RecentKind.File ? 10 : 5).ToArray();
            if (entries.Length == 0) continue;
            if (OpenRecentMenu.Items.Count > 0) OpenRecentMenu.Items.Add(new Separator());
            foreach (var entry in entries)
            {
                var name = kind == RecentKind.Url ? entry.Path : System.IO.Path.GetFileName(entry.Path);
                // A text block keeps underscores in file names. Plain menu text would treat them as shortcut keys.
                var item = new MenuItem { Header = new TextBlock { Text = string.IsNullOrEmpty(name) ? entry.Path : name }, Tag = entry };
                AutomationProperties.SetName(item, entry.Path);
                ToolTip.SetTip(item, entry.Path);
                item.Click += async (_, e) =>
                {
                    e.Handled = true;
                    if (kind == RecentKind.Workspace) await OpenRecentWorkspaceAsync(entry.Path);
                    else if (kind == RecentKind.Url) await OpenStoredUrlAsync(entry.Path);
                    else await OpenDocumentPathAsync(entry.Path);
                };
                OpenRecentMenu.Items.Add(item);
            }
        }
        if (OpenRecentMenu.Items.Count > 0) OpenRecentMenu.Items.Add(new Separator());
        var more = new MenuItem { Header = "More…" };
        more.Click += OnRecentMoreClick;
        OpenRecentMenu.Items.Add(more);
        var discarded = new MenuItem { Tag = CommandIds.RecentlyDiscarded };
        RefreshMenuCommand(discarded);
        discarded.Click += OnMenuCommandClick;
        OpenRecentMenu.Items.Add(discarded);
        OpenRecentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Tag = CommandIds.ClearRecent };
        RefreshMenuCommand(clear);
        clear.Click += OnMenuCommandClick;
        OpenRecentMenu.Items.Add(clear);
    }
}
