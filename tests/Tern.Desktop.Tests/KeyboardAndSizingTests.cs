using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class KeyboardAndSizingTests
{
    [Fact]
    public void AltGr_is_Ctrl_plus_right_Alt_down()
    {
        Assert.True(AltGrGuard.IsAltGrDown(vk => vk is 0xA2 or 0xA5 ? (short)-128 : (short)0));
        Assert.False(AltGrGuard.IsAltGrDown(vk => vk == 0xA2 ? (short)-128 : (short)0));
        Assert.False(AltGrGuard.IsAltGrDown(_ => 0));
    }

    [AvaloniaFact]
    public void Ctrl_Alt_printable_command_is_ignored_while_AltGr_is_down_but_works_otherwise()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("A note");
        Assert.True(f.Shell.Registry.SetBinding(CommandIds.AskAI, "Ctrl+Alt+K").Succeeded);
        f.Window.ApplySettings(); Pump(f);
        try
        {
            AltGrGuard.KeyStateOverride = _ => -128;
            Press(f.Window, Key.K, KeyModifiers.Control | KeyModifiers.Alt);
            Pump(f);
            Assert.False(f.Window.IsOverlayOpen);
            AltGrGuard.KeyStateOverride = _ => 0;
            Press(f.Window, Key.K, KeyModifiers.Control | KeyModifiers.Alt);
            Pump(f);
            Assert.True(f.Window.IsOverlayOpen);
        }
        finally { AltGrGuard.KeyStateOverride = null; }
    }

    [Fact]
    public void Shortcut_editor_warns_only_for_AltGr_colliding_gestures()
    {
        Assert.Contains("AltGr", ShortcutsWindow.AltGrWarning("Ctrl+Alt+S"));
        Assert.Equal(string.Empty, ShortcutsWindow.AltGrWarning("Ctrl+Shift+S"));
        Assert.Equal(string.Empty, ShortcutsWindow.AltGrWarning("Ctrl+Alt+F10"));
        Assert.Equal(string.Empty, ShortcutsWindow.AltGrWarning(null));
    }

    [Fact]
    public void Window_fits_a_1366x768_screen_at_150_percent()
    {
        var fit = WindowSizing.Fit(910, 485, 1.5, WindowSizing.DefaultWidth, WindowSizing.DefaultHeight);
        Assert.True(fit.Maximize);
        Assert.True(fit.Width <= 910 && fit.Height <= 485);
        Assert.True(fit.MinWidth <= 910 && fit.MinHeight <= 485);
    }

    [Fact]
    public void Window_keeps_default_size_on_a_large_screen()
    {
        var fit = WindowSizing.Fit(2560, 1400, 1, WindowSizing.DefaultWidth, WindowSizing.DefaultHeight);
        Assert.False(fit.Maximize);
        Assert.Equal(WindowSizing.DefaultWidth, fit.Width);
        Assert.Equal(720, fit.MinWidth);
    }

    [AvaloniaFact]
    public async Task Editor_takes_at_least_half_the_window_at_ui_scale_2_on_1366x768()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("# Note\n");
        f.Shell.Settings.UiScale = 2;
        f.Window.Width = 1366; f.Window.Height = 768;
        f.Window.ApplySettings(); Pump(f);
        await f.Window.RunCommandAsync(CommandIds.ViewSource); Pump(f);
        Assert.True(f.Window.LibraryAutoCollapsed);
        Assert.True(f.Window.EditorControl.Bounds.Width >= f.Window.Bounds.Width * .5, $"editor {f.Window.EditorControl.Bounds.Width} of {f.Window.Bounds.Width}");
    }

    [AvaloniaFact]
    public void Document_text_scales_with_the_interface_only_when_the_option_is_on()
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.UiScale = 2;
        f.Shell.Settings.EditorFontSize = 14; f.Shell.Settings.PreviewFontSize = 16;
        f.Window.ApplySettings(); Pump(f);
        Assert.Equal(14, f.Window.EditorControl.FontSize);
        Assert.Equal(16, f.Window.PreviewControl.FontSize);
        f.Shell.Settings.ScaleDocumentText = true;
        f.Window.ApplySettings(); Pump(f);
        Assert.Equal(28, f.Window.EditorControl.FontSize);
        Assert.Equal(32, f.Window.PreviewControl.FontSize);
        Assert.Equal(14, f.Shell.Settings.EditorFontSize);
    }

    [AvaloniaFact]
    public void Caption_buttons_are_named_and_the_maximize_button_reflects_window_state()
    {
        using var f = new DesktopFixture();
        var maximize = f.Window.FindControl<Button>("MaximizeButton")!;
        Assert.Equal("Minimize", AutomationProperties.GetName(f.Window.FindControl<Button>("MinimizeButton")!));
        Assert.Equal("Close", AutomationProperties.GetName(f.Window.FindControl<Button>("CloseWindowButton")!));
        f.Window.WindowState = WindowState.Maximized; Pump(f);
        Assert.Equal("Restore down", AutomationProperties.GetName(maximize));
        f.Window.WindowState = WindowState.Normal; Pump(f);
        Assert.Equal("Maximize", AutomationProperties.GetName(maximize));
    }

    private static void Press(InputElement target, Key key, KeyModifiers modifiers) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });

    private static void Pump(DesktopFixture f)
    {
        f.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
