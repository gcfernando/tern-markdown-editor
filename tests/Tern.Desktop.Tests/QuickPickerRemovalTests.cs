using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Shell;
using Tern.Desktop.Views;
using Tern.Domain.Text;
using Tern.Workspaces;
using Tern.App.Settings;
using Avalonia.Media;

namespace Tern.Desktop.Tests;

public sealed class QuickPickerRemovalTests
{
    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper)]
    [InlineData(ThemeChoice.Midnight)]
    public async Task Both_remove_icons_use_the_current_theme_danger_color(ThemeChoice theme)
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.Theme = theme;
        fixture.Window.ApplyTheme();
        await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("reference.md", "Reference"));
        await fixture.Window.ShowNavAsync("Files");
        fixture.Window.ShowQuickOpen("");
        Pump(fixture);
        Assert.True(Application.Current!.TryGetResource("Tern.Danger", Application.Current.ActualThemeVariant, out var danger));
        var expected = Assert.IsAssignableFrom<ISolidColorBrush>(danger).Color;
        var buttons = fixture.Window.GetVisualDescendants().OfType<Button>().Where(b => AutomationProperties.GetName(b) == "Remove file reference" && b.IsEffectivelyVisible).ToArray();
        Assert.Equal(2, buttons.Length);
        Assert.All(buttons, button =>
        {
            var icon = Assert.IsType<Avalonia.Controls.Shapes.Path>(button.Content);
            Assert.Equal(expected, Assert.IsAssignableFrom<ISolidColorBrush>(icon.Stroke).Color);
        });
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Quick_open_remove_preserves_dirty_tabs_and_does_not_accept_or_dismiss(bool missing)
    {
        using var fixture = new DesktopFixture();
        var path = await fixture.WriteAsync("picker-reference.md", "Saved\n");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(0, "unsaved ")]);
        var active = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("active.md", "Active\n")))!;
        if (missing) File.Delete(path);
        fixture.Window.ShowQuickOpen("");
        Pump(fixture);
        var picker = fixture.Window.Picker!;
        var item = Assert.Single(picker.Results.Items.OfType<PickerItem>(), r => Equals(r.Payload, path));
        Assert.NotNull(item.RemoveReference);
        var remove = Assert.Single(picker.Results.GetVisualDescendants().OfType<Button>(), b => Equals(b.DataContext, item) && AutomationProperties.GetName(b) == "Remove file reference");
        Assert.NotNull(Assert.IsType<Avalonia.Controls.Shapes.Path>(remove.Content).Data);
        Assert.True(remove.Bounds.Width >= 32 && remove.Bounds.Height >= 36);
        Assert.True(remove.TranslatePoint(default, picker)!.Value.X > picker.Bounds.Width / 2);
        if (!missing) Capture(fixture, "quick-open-before-remove.png");
        var point = remove.TranslatePoint(new Point(remove.Bounds.Width / 2, remove.Bounds.Height / 2), fixture.Window)!.Value;
        Assert.True(point.X > 0 && point.X < fixture.Window.ClientSize.Width);
        fixture.Window.MouseMove(point);
        fixture.Window.MouseDown(point, MouseButton.Left);
        fixture.Window.MouseUp(point, MouseButton.Left);
        Pump(fixture);
        Assert.True(fixture.Window.IsOverlayOpen);
        Assert.Same(active, fixture.Shell.ActiveTab);
        Assert.Contains(tab, fixture.Shell.Tabs);
        Assert.Equal("unsaved Saved\n", tab.CurrentText);
        Assert.True(tab.IsDirty);
        Assert.DoesNotContain(picker.Results.Items.OfType<PickerItem>(), r => Equals(r.Payload, path));
        Assert.DoesNotContain(new RecentItemsStore(AppPaths.Under(fixture.AppData).StateDirectory).Files, r => r.Path == path);
        Assert.Equal(0, fixture.Dialogs.SavePrompts);
        Assert.Equal(!missing, File.Exists(path));
        if (!missing) Assert.Equal("Saved\n", await File.ReadAllTextAsync(path));
        Capture(fixture, missing ? "quick-open-missing-reference.png" : "quick-open-remove-reference.png");
    }

    [AvaloniaFact]
    public async Task Quick_open_remove_reports_persistence_failure_without_closing_the_picker()
    {
        using var fixture = new DesktopFixture();
        var path = await fixture.WriteAsync("kept.md", "Kept\n");
        await fixture.Shell.OpenFileAsync(path);
        fixture.Window.ShowQuickOpen("");
        Pump(fixture);
        var recentPath = Path.Combine(AppPaths.Under(fixture.AppData).StateDirectory, RecentItemsStore.FileName);
        using var preventReplacement = new FileStream(recentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var item = Assert.Single(fixture.Window.Picker!.Results.Items.OfType<PickerItem>());
        Assert.NotNull(item.RemoveReference);
        item.RemoveReference();
        Pump(fixture);
        Assert.True(fixture.Window.IsOverlayOpen);
        Assert.NotNull(fixture.Shell.Recent.LastSaveError);
        Assert.Contains("could not be saved", fixture.Shell.StatusMessage, StringComparison.Ordinal);
        Assert.Contains(new RecentItemsStore(AppPaths.Under(fixture.AppData).StateDirectory).Files, r => r.Path == path);
        Assert.Equal("Kept\n", await File.ReadAllTextAsync(path));
    }

    [AvaloniaFact]
    public async Task Workspace_files_and_command_palette_do_not_offer_reference_removal()
    {
        using var fixture = new DesktopFixture();
        await fixture.WriteAsync("workspace.md", "# Heading\n");
        await fixture.Shell.OpenWorkspaceAsync(fixture.Docs);
        fixture.Window.ShowQuickOpen("");
        Pump(fixture);
        Assert.NotEmpty(fixture.Window.Picker!.Results.Items.OfType<PickerItem>());
        Assert.All(fixture.Window.Picker.Results.Items.OfType<PickerItem>(), r => Assert.Null(r.RemoveReference));
        Assert.DoesNotContain(fixture.Window.Picker.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "Remove file reference");
        fixture.Window.ShowPalette();
        Pump(fixture);
        Assert.All(fixture.Window.Picker.Results.Items.OfType<PickerItem>(), r => Assert.Null(r.RemoveReference));
        Assert.DoesNotContain(fixture.Window.Picker.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "Remove file reference");
    }

    private static void Pump(DesktopFixture fixture)
    {
        fixture.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        fixture.Window.UpdateLayout();
    }

    private static void Capture(DesktopFixture fixture, string name)
    {
        var folder = Path.Combine(Screenshots.Folder, "beginner-ui");
        Directory.CreateDirectory(folder);
        using var frame = fixture.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(folder, name));
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
