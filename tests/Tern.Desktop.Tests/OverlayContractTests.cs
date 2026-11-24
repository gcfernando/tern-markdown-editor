using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.Desktop.Views;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class OverlayContractTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Escape_closes_activity_protection_and_quick_start_overlays_and_returns_focus(bool reduceMotion)
    {
        var reader = MotionPolicy.SystemAnimationsReader;
        MotionPolicy.SystemAnimationsReader = () => true;
        try
        {
            using var f = new DesktopFixture();
            f.Shell.Settings.ReduceMotion = reduceMotion;
            f.Window.ApplySettings();
            f.Shell.NewDocument("note");
            Pump(f);

            void OpenActivity() => f.Window.ActivityButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void OpenProtection() => f.Window.GetVisualDescendants().OfType<Button>()
                .First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Document save and recovery details")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void OpenWelcome() => _ = f.Window.RunCommandAsync(CommandIds.Welcome);

            foreach (var (name, open) in new (string, Action)[] { ("activity", OpenActivity), ("protection", OpenProtection), ("welcome", OpenWelcome) })
            {
                f.Window.Editor.TextArea.Focus();
                Pump(f);
                var before = f.Window.FocusManager?.GetFocusedElement();
                Assert.NotNull(before);
                open();
                Pump(f);
                Assert.True(f.Window.IsOverlayOpen, "open " + name);
                Press(f.Window, Key.Escape, KeyModifiers.None);
                Pump(f);
                Assert.False(f.Window.IsOverlayOpen, "closed " + name);
                Assert.Same(before, f.Window.FocusManager?.GetFocusedElement());
            }
            await Task.CompletedTask;
        }
        finally { MotionPolicy.SystemAnimationsReader = reader; MotionPolicy.Apply(false); }
    }

    [AvaloniaFact]
    public void Self_handling_overlays_are_not_closed_by_the_window_escape_rule()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("note");
        f.Window.ShowPalette();
        Pump(f);
        Press(f.Window, Key.Escape, KeyModifiers.None);
        Assert.True(f.Window.IsOverlayOpen);
        Press(f.Window.Picker!.Input, Key.Escape, KeyModifiers.None);
        Assert.False(f.Window.IsOverlayOpen);
    }

    [AvaloniaFact]
    public void Registry_shortcuts_and_menu_access_are_suspended_while_an_overlay_is_open()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("note");
        Pump(f);
        f.Window.ActivityButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(f);
        var gesture = f.Shell.Registry.GetGesture(CommandIds.NewFile)!;
        Assert.True(Tern.App.Commands.Gesture.TryParse(gesture, out var parsed));
        var tabs = f.Shell.Tabs.Count;
        var key = Enum.Parse<Key>(parsed.Key);
        var modifiers = (parsed.Ctrl ? KeyModifiers.Control : 0) | (parsed.Alt ? KeyModifiers.Alt : 0) | (parsed.Shift ? KeyModifiers.Shift : 0);
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
        f.Window.RaiseEvent(args);
        Pump(f);
        Assert.Equal(tabs, f.Shell.Tabs.Count);
        Assert.True(f.Window.IsOverlayOpen);

        var alt = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.LeftAlt, KeyModifiers = KeyModifiers.Alt };
        f.Window.RaiseEvent(alt);
        Assert.True(alt.Handled);
    }

    [AvaloniaFact]
    public void Overlay_layer_spans_every_window_row()
    {
        using var f = new DesktopFixture();
        Pump(f);
        var layer = f.Window.OverlayLayer;
        Assert.Equal(0, Grid.GetRow(layer));
        Assert.Equal(5, Grid.GetRowSpan(layer));
        Assert.False(ReferenceEquals(layer.Parent, f.Window.Body));
        Assert.Equal(f.Window.TitleBar.GetVisualParent(), layer.GetVisualParent());
    }

    [AvaloniaFact]
    public void Protection_retry_closes_the_overlay_before_saving()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("note");
        Pump(f);
        f.Window.GetVisualDescendants().OfType<Button>()
            .First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Document save and recovery details")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(f);
        var saveAs = f.Window.OverlayLayer.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Save as…");
        saveAs.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(f.Window.IsOverlayOpen);
    }

    [AvaloniaFact]
    public void Help_buttons_are_enabled_without_a_document_and_describe_help()
    {
        using var f = new DesktopFixture();
        Pump(f);
        var helps = f.Window.GetVisualDescendants().OfType<Button>().Where(b => Help.GetFeature(b) is not null).ToArray();
        Assert.True(helps.Length >= 3);
        foreach (var button in helps)
        {
            Assert.Null(button.Tag);
            Assert.True(button.IsEnabled);
            Assert.StartsWith("Help", ToolTip.GetTip(button) as string);
        }
        f.Shell.NewDocument("x");
        Pump(f);
        Assert.All(f.Window.GetVisualDescendants().OfType<Button>().Where(b => Help.GetFeature(b) is not null), b => Assert.True(b.IsEnabled));
    }

    private static void Press(InputElement target, Key key, KeyModifiers modifiers) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });

    private static void Pump(DesktopFixture f)
    {
        f.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
