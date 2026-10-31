using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;

namespace Tern.Desktop.Tests;

public sealed class PanelRestoreIconTests
{
    [AvaloniaTheory]
    [InlineData("Expand Library", "Sidebar")]
    [InlineData("Expand Inspector", "RightPanel")]
    public async Task Restore_icon_exits_focus_mode_and_opens_the_requested_panel_in_one_click(string iconName, string panelName)
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.NewDocument("# Note");
        await fixture.Window.RunCommandAsync(CommandIds.ToggleFocus);
        Pump(fixture.Window);
        Assert.True(fixture.Window.IsFocusMode);
        var restore = FindButton(fixture.Window, iconName);
        Assert.True(restore.IsEffectivelyVisible);
        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(fixture.Window);
        Assert.False(fixture.Window.IsFocusMode);
        Assert.True(fixture.Window.FindControl<Control>(panelName)!.IsEffectivelyVisible);
        Assert.False(restore.IsVisible);
    }

    [AvaloniaTheory]
    [InlineData("Collapse Library", "Expand Library", "Sidebar")]
    [InlineData("Collapse Inspector", "Expand Inspector", "RightPanel")]
    public void Collapsed_panel_has_a_visible_clickable_restore_icon(string collapseName, string expandName, string panelName)
    {
        using var fixture = new DesktopFixture();
        Pump(fixture.Window);
        var panel = fixture.Window.FindControl<Control>(panelName)!;
        Assert.True(panel.IsEffectivelyVisible);
        FindButton(fixture.Window, collapseName).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(fixture.Window);
        Assert.False(panel.IsVisible);
        var restore = FindButton(fixture.Window, expandName);
        Assert.True(restore.IsEffectivelyVisible);
        Assert.True(restore.IsEnabled);
        Assert.IsType<Avalonia.Controls.Shapes.Path>(restore.Content);
        Assert.True(restore.Bounds.Width >= 28 && restore.Bounds.Height >= 28);
        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(fixture.Window);
        Assert.True(panel.IsEffectivelyVisible);
        Assert.False(restore.IsVisible);
    }

    [AvaloniaFact]
    public async Task Collapsed_reading_preview_has_a_restore_icon_and_visible_reading_mode_button()
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.NewDocument("# A note\n\nText");
        await fixture.Window.RunCommandAsync(CommandIds.ViewSplit);
        Pump(fixture.Window);
        FindButton(fixture.Window, "Collapse Reading preview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(fixture.Window);
        Assert.False(fixture.Window.PreviewControl.IsEffectivelyVisible);
        var restore = FindButton(fixture.Window, "Expand Reading preview");
        Assert.True(restore.IsEffectivelyVisible);
        Assert.True(restore.IsEnabled);
        Assert.IsType<Avalonia.Controls.Shapes.Path>(restore.Content);
        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(fixture.Window);
        Assert.True(fixture.Window.PreviewControl.IsEffectivelyVisible);
        Assert.False(restore.IsVisible);
        FindButton(fixture.Window, "Collapse Reading preview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(fixture.Window);
        var reading = fixture.Window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("ReadingButton")!;
        Assert.True(reading.IsEffectivelyVisible);
        Assert.True(reading.IsEnabled);
        reading.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(fixture.Window);
        Assert.True(fixture.Window.PreviewControl.IsEffectivelyVisible);
    }

    private static Button FindButton(Window window, string name) =>
        Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == name);

    private static void Pump(Window window)
    {
        for (var i = 0; i < 4; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    }
}
