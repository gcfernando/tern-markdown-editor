using Avalonia.Interactivity;
using Tern.App.Documents;
using Tern.App.Settings;
using Tern.App.Shell;
using Tern.Desktop.Services;
using Tern.Rendering.Preview;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private readonly RemoteImageAccess _imageAccess;
    private DocumentTab? _remoteConsentTab;
    private static string ImageKey(DocumentTab tab) => RemoteImageAccess.DocumentKey(tab.FilePath, tab.Session.Id);
    private Func<string, byte[]?>? ExportImageLoader(DocumentTab tab)
    {
        var key = ImageKey(tab);
        return _remoteImages is null ? null : destination => _remoteImages.FetchForDocument(key, destination);
    }

    private void BindRemoteImages(DocumentTab tab, PreviewDocument document)
    {
        var key = ImageKey(tab);
        Preview.RemoteImageLoader = _remoteImages is null ? null : (url, token) => _remoteImages.LoadForDocumentAsync(key, url, token);
        RefreshRemoteImageBanner(tab, RemoteImageCount.Count(document));
    }

    private void RefreshSourceRemoteImageConsent()
    {
        if (_shown is not { } tab) { RemoteImagesBanner.IsVisible = false; return; }
        var count = tab.Analysis?.RemoteImageCount ?? 0;
        RefreshRemoteImageBanner(tab, count);
    }

    private void RefreshRemoteImageBanner(DocumentTab tab, int count)
    {
        _remoteConsentTab = tab;
        if (count == 0) { RemoteImagesBanner.IsVisible = false; return; }
        var key = ImageKey(tab);
        var blocked = count > 0 && !_imageAccess.Allows(key);
        RemoteImagesBanner.IsVisible = blocked;
        var ask = !_shell.Settings.OfflineMode && _shell.Settings.RemoteImages == RemoteImagePolicy.Ask;
        RemoteImagesText.Text = _shell.Settings.OfflineMode ? "Offline mode: remote images are blocked."
            : _shell.Settings.RemoteImages == RemoteImagePolicy.Never ? "Remote images are blocked in Settings."
            : $"This document loads {count} remote images. Loading them contacts their hosts.";
        LoadRemoteImagesOnce.IsVisible = ask;
        AlwaysLoadRemoteImages.IsVisible = ask && tab.FilePath is not null;
    }

    private void OnLoadRemoteImagesOnce(object? sender, RoutedEventArgs e) => ApproveRemoteImages(false);
    private void OnAlwaysLoadRemoteImages(object? sender, RoutedEventArgs e) => ApproveRemoteImages(true);
    private void ApproveRemoteImages(bool always)
    {
        if (_shown is not { } tab || !ReferenceEquals(tab, _remoteConsentTab) || _shell.Settings.OfflineMode || _shell.Settings.RemoteImages != RemoteImagePolicy.Ask) return;
        if (always && tab.FilePath is not null) { _imageAccess.AllowAlways(ImageKey(tab)); _shell.SaveSettings(); }
        else _imageAccess.AllowOnce(ImageKey(tab));
        RebuildPreview();
    }

    private bool RequireNetwork()
    {
        if (!_shell.Settings.OfflineMode) return true;
        _shell.SetStatus("Offline mode blocks URL import, AI, MCP and Copilot. Turn Offline off to connect.", StatusSeverity.Warning);
        return false;
    }

    private void OnOfflineToggle(object? sender, RoutedEventArgs e)
    {
        _shell.Settings.OfflineMode = OfflineToggle.IsChecked == true;
        var saved = _shell.SaveSettings();
        RefreshNetworkState();
        if (saved) _shell.SetStatus(_shell.Settings.OfflineMode ? "Offline mode on. Tern will not use the internet." : "Offline mode off. Tern still asks before loading online pictures, unless you changed that setting.", StatusSeverity.Info);
        RebuildPreview();
    }

    private void RefreshNetworkState()
    {
        OfflineToggle.IsChecked = _shell.Settings.OfflineMode;
        AIConnectionAction.IsEnabled = !_shell.Settings.OfflineMode && _aiWorkspace.SelectedConnection is not null;
        Avalonia.Controls.ToolTip.SetTip(AIConnectionAction, _shell.Settings.OfflineMode ? "Offline mode blocks AI and MCP. Turn Offline off to connect." : "Use the selected connection");
        Avalonia.Controls.ToolTip.SetShowOnDisabled(AIConnectionAction, true);
        RefreshCommandAvailability();
        if (_shell.Settings.OfflineMode) foreach (var request in _onlineRequests.Values.ToArray()) request.Cancel();
        if (_shown is null) RemoteImagesBanner.IsVisible = false;
    }

    private MarkdownUrlLoader CreateOnlineLoader()
    {
        var loader = OnlineLoaderFactory();
        loader.NetworkAllowed = () => !_shell.Settings.OfflineMode;
        return loader;
    }
}
