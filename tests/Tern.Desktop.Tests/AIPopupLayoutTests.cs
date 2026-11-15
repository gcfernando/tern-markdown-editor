using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class AIPopupLayoutTests
{
    public static IEnumerable<object[]> Layouts => from size in new[] { (900, 560), (1280, 800) }
        from scale in new[] { 1d, 1.5, 2d }
        from theme in new[] { ThemeChoice.Midnight, ThemeChoice.Paper }
        select new object[] { size.Item1, size.Item2, scale, theme };

    [AvaloniaTheory]
    [MemberData(nameof(Layouts))]
    public void Actual_popup_wraps_long_details_without_horizontal_scrolling(int width, int height, double scale, ThemeChoice theme)
    {
        var path = @"C:\Demo\tools\" + string.Join('\\', Enumerable.Repeat("long-installation-directory", 10)) + @"\copilot.exe";
        using var f = new DesktopFixture(directory =>
        {
            var vault = new Vault();
            new IntegrationProfileStore(directory, vault).Save(new IntegrationProfile(Guid.NewGuid().ToString("N"), "copilot",
                width == 1280 && scale == 1 ? "GitHub Copilot" : "GitHub Copilot " + new string('X', 65), path), "");
            return new AIWorkspaceService(directory, vault, createCopilotClient: () => throw new InvalidOperationException("Opening a popup must not contact AI."));
        });
        f.Window.Width = width; f.Window.Height = height;
        f.Shell.Settings.Theme = theme; f.Shell.Settings.UiScale = scale;
        f.Window.ApplyTheme(); f.Window.ApplySettings();
        f.Window.AIEntryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(f);
        var root = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(f.Window.AIBar));
        Assert.True(f.Window.IsAIPopupOpen);
        // The headless platform hosts real flyouts in an overlay; native Windows may use PopupRoot.
        var presenter = Assert.Single(f.Window.AIBar.GetVisualAncestors().OfType<FlyoutPresenter>());
        Assert.Contains("tern-popup", presenter.Classes);
        Assert.False(f.Window.AIConnectionDetails.IsExpanded);
        Assert.DoesNotContain(path, f.Window.AIConnectionSummary.Text);
        Assert.Contains(path, f.Window.AIConnectionDetailsText.Text);
        CheckWidth(presenter);
        Capture(root, $"{theme}-{width}-{height}-{scale}-collapsed");

        f.Window.AIConnectionDetails.IsExpanded = true;
        Pump(f);
        CheckWidth(presenter);
        Assert.True(f.Window.AIConnectionDetailsText.TextLayout.TextLines.Count > 1);
        foreach (var button in new[] { f.Window.AIConnectionAction, f.Window.AIManageConnections, f.Window.AIAskWorkspace })
        {
            var point = button.TranslatePoint(default, f.Window.AIBar)!.Value;
            Assert.InRange(point.X, -1, f.Window.AIBar.Bounds.Width - button.Bounds.Width + 1);
            Assert.InRange(point.Y, -1, f.Window.AIBar.Bounds.Height - button.Bounds.Height + 1);
        }
        Assert.Equal(f.Window.AIManageConnections.Bounds.Height, f.Window.AIAskWorkspace.Bounds.Height, 1);
        f.Window.AIPopupScroll.Offset = default;
        Pump(f);
        Capture(root, $"{theme}-{width}-{height}-{scale}-details");
        f.Window.AIConnectionDetails.IsExpanded = false;
        f.Window.AIPopupScroll.Offset = default;
        Pump(f);
        f.Window.AIEntryClose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(f);
        Assert.False(f.Window.IsAIPopupOpen);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_and_tool_connections_keep_the_same_panel_structure(bool tools)
    {
        using var f = new DesktopFixture(directory =>
        {
            var vault = new Vault();
            if (tools) new IntegrationProfileStore(directory, vault).Save(new IntegrationProfile(Guid.NewGuid().ToString("N"), "mcp", "Research tools", "https://tools.example/mcp"), "");
            return new AIWorkspaceService(directory, vault);
        });
        f.Window.AIEntryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(f);
        Assert.Equal(tools, f.Window.AIConnectionAction.IsEnabled);
        Assert.Equal(tools, f.Window.AIConnectionDetails.IsVisible);
        Assert.Equal(tools ? "Open MCP tools" : "Write with AI", f.Window.AIConnectionAction.Content);
        Assert.Equal(f.Window.AIManageConnections.Bounds.Width, f.Window.AIAskWorkspace.Bounds.Width, 1);
        Assert.True(f.Window.AIConnectionAction.Bounds.Width > f.Window.AIManageConnections.Bounds.Width);
        CheckWidth(Assert.Single(f.Window.AIBar.GetVisualAncestors().OfType<FlyoutPresenter>()));
        f.Window.AIEntryClose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    [AvaloniaFact]
    public async Task Compact_search_options_reuse_the_same_popup_shell()
    {
        using var f = new DesktopFixture();
        f.Window.Width = 900;
        f.Shell.NewDocument("Find this note");
        f.Window.ShowFindBar(false);
        await f.Window.SearchAsync("note");
        f.Window.FindOptionsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(f);
        var presenter = Assert.Single(f.Window.FindOptionsPanel.GetVisualAncestors().OfType<FlyoutPresenter>());
        Assert.Contains("tern-popup", presenter.Classes);
        Assert.True(f.Window.CaseToggle.IsEffectivelyVisible);
        Assert.True(f.Window.WordToggle.IsEffectivelyVisible);
        Assert.True(f.Window.RegexToggle.IsEffectivelyVisible);
        CheckWidth(presenter);
    }

    private static void CheckWidth(Visual root)
    {
        Assert.DoesNotContain(root.GetVisualDescendants().OfType<ScrollBar>(), b => b.Orientation == Orientation.Horizontal && b.IsEffectivelyVisible);
        foreach (var scroll in root.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible))
        {
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1,
                $"Horizontal overflow: {scroll.Name} extent={scroll.Extent.Width}, viewport={scroll.Viewport.Width}");
            Assert.Equal(0, scroll.Offset.X);
        }
    }

    private static void Pump(DesktopFixture f)
    {
        for (var i = 0; i < 6; i++) { f.Window.UpdateLayout(); TopLevel.GetTopLevel(f.Window.AIBar)?.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    }

    private static void Capture(TopLevel root, string name)
    {
        var folder = Path.Combine(Screenshots.Folder, "ai-popup-fix");
        Directory.CreateDirectory(folder);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
        using var frame = root.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(folder, name + ".png"));
    }

    private sealed class Vault : IAICredentialStore
    {
        public string? Read(string id) => null;
        public void Save(string id, string secret) { }
        public void Remove(string id) { }
    }
}
