using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.ViewModels;
using Tern.App.Settings;

namespace Tern.Desktop.Tests;

public sealed class WorkspaceAlignmentTests
{
    [AvaloniaTheory]
    [InlineData(1, 280)]
    [InlineData(1.5, 280)]
    [InlineData(2, 280)]
    [InlineData(1.5, 180)]
    [InlineData(2, 360)]
    public async Task Nested_cards_share_right_edge_and_do_not_overlap_the_scrollbar(double scale, double width)
    {
        using var fixture = new DesktopFixture();
        await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Company");
        var company = fixture.Shell.Workspace!.Root;
        await fixture.Window.CreateWorkspaceEntryAsync(false, "Welcome.md");
        await fixture.Window.CreateWorkspaceAtAsync(company, "HR");
        await fixture.Window.CreateWorkspaceEntryAsync(false, "People.md");
        await fixture.Window.CreateWorkspaceAtAsync(fixture.Docs, "Diagrams");
        await fixture.Window.CreateWorkspaceEntryAsync(false, "Flow.md");
        fixture.Shell.Settings.UiScale = scale;
        fixture.Shell.Settings.SidebarWidth = width;
        fixture.Shell.Settings.Theme = ThemeChoice.Midnight;
        fixture.Window.ApplySettings();
        fixture.Window.ApplyTheme();
        fixture.Shell.SelectWorkspace(fixture.Shell.OpenWorkspaces.Single(workspace => workspace.Root == company));
        await fixture.Window.ShowNavAsync("Files");
        Pump(fixture);

        var tree = fixture.Window.FileTree;
        var scroller = Assert.Single(tree.GetVisualDescendants().OfType<ScrollViewer>());
        Assert.False(scroller.AllowAutoHide);
        var viewport = Assert.Single(scroller.GetVisualDescendants().OfType<ScrollContentPresenter>());
        var viewportLeft = viewport.TranslatePoint(default, fixture.Window)!.Value.X;
        var viewportRight = viewportLeft + viewport.Bounds.Width;
        var cards = tree.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Name == "WorkspaceSectionCard" && border.IsEffectivelyVisible).ToArray();
        Assert.Equal(3, cards.Length);
        var companyCard = Assert.Single(cards, card => ((TreeRow)card.DataContext!).Name == "Company");
        var hrCard = Assert.Single(cards, card => ((TreeRow)card.DataContext!).Name == "HR");
        var diagramsCard = Assert.Single(cards, card => ((TreeRow)card.DataContext!).Name == "Diagrams");
        Rect Bounds(Control control) => new(control.TranslatePoint(default, fixture.Window)!.Value, control.Bounds.Size);
        var rootBounds = Bounds(companyCard);
        Assert.InRange(rootBounds.Left - viewportLeft, 0, 2);
        Assert.InRange(Math.Abs(Bounds(diagramsCard).Left - rootBounds.Left), 0, 1);
        Assert.InRange(Bounds(hrCard).Left - rootBounds.Left, 15, 17);
        foreach (var card in cards)
        {
            var bounds = Bounds(card);
            Assert.InRange(Math.Abs(bounds.Right - rootBounds.Right), 0, 1);
            Assert.True(bounds.Right <= viewportRight - 3, "Card border must stay clear of the scrollbar gutter.");
            var row = (TreeRow)card.DataContext!;
            var container = card.FindAncestorOfType<TreeViewItem>()!;
            var expanderGutter = container.GetVisualDescendants().OfType<Panel>()
                .First(panel => panel.Name == "PART_ExpandCollapseChevronContainer");
            Assert.False(expanderGutter.IsVisible);
            foreach (var action in card.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible))
            {
                var actionBounds = Bounds(action);
                Assert.True(actionBounds.Left >= bounds.Left && actionBounds.Right <= bounds.Right,
                    $"{row.Name}: {AutomationProperties.GetName(action)} overflows its card at {scale} / {width}.");
            }
        }
        var folder = Path.Combine(Screenshots.Folder, "workspace-alignment");
        Directory.CreateDirectory(folder);
        using var frame = fixture.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(folder, $"stack-{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{width}.png"));
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void Pump(DesktopFixture fixture)
    {
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
