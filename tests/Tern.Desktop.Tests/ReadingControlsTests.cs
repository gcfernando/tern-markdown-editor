using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Settings;
using Tern.Desktop.Themes;

namespace Tern.Desktop.Tests;

public sealed class ReadingControlsTests
{
    [AvaloniaFact]
    public async Task Reading_controls_and_view_shortcuts_are_visible_and_follow_custom_bindings()
    {
        using var f = new DesktopFixture();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Tern.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var text = await File.ReadAllTextAsync(Path.Combine(root.FullName, "tests", "Fixtures", "MarkdownSamples", "Data_Science_Handbook.md"));
        var path = await f.WriteAsync("handbook.md", text);
        var tab = (await f.Shell.OpenFileAsync(path))!;
        await f.Window.RunCommandAsync(CommandIds.ViewReading);
        await f.Window.RebuildPreviewAsync();
        ThemeManager.Apply(Application.Current!, ThemeChoice.Midnight);
        Pump(f);
        foreach (var (name, key) in new[] { ("SourceButton", "Ctrl+1"), ("SplitButton", "Ctrl+2"), ("ReadingButton", "Ctrl+3"), ("FocusButton", "F8") })
            Assert.Contains(key, ToolTip.GetTip(f.Window.FindControl<Control>(name)!)!.ToString());
        Assert.True(f.Shell.Registry.SetBinding(CommandIds.ViewReading, "Ctrl+Alt+F9").Succeeded);
        f.Window.ApplySettings(); Pump(f);
        Assert.Contains("Ctrl+Alt+F9", ToolTip.GetTip(f.Window.FindControl<Control>("ReadingButton")!)!.ToString());
        Button Find(string name) => Assert.Single(f.Window.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == name);
        Find("Next reading page").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(f);
        Assert.True(f.Window.PreviewControl.TopSourceLine > 1);
        Find("Go to document beginning").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(f);
        Assert.Equal(1, f.Window.PreviewControl.TopSourceLine);
        f.Window.PreviewControl.ScrollToSourceLine(92); Pump(f);
        var output = Path.Combine(root.FullName, "artifacts", "screenshots", "reading-mode"); Directory.CreateDirectory(output);
        using var frame = f.Window.CaptureRenderedFrame(); Assert.NotNull(frame);
        using (var file = File.Create(Path.Combine(output, "handbook-reading-window.png"))) frame.Save(file, new PngBitmapEncoderOptions());
        Assert.Equal(text, tab.CurrentText); Assert.False(tab.IsDirty);
    }

    [Fact]
    public void Every_default_shortcut_is_understood_by_the_desktop_keyboard_parser()
    {
        foreach (var command in CommandRegistry.DefaultCommands())
        {
            Assert.True(Gesture.TryParse(command.DefaultGesture, out var expected));
            var actual = Tern.Desktop.Views.MainWindow.DesktopGesture(command.DefaultGesture);
            Assert.NotNull(actual);
            Assert.Equal(Enum.Parse<Key>(expected.Key), actual.Key);
            Assert.Equal(expected.Ctrl, actual.KeyModifiers.HasFlag(KeyModifiers.Control));
            Assert.Equal(expected.Alt, actual.KeyModifiers.HasFlag(KeyModifiers.Alt));
            Assert.Equal(expected.Shift, actual.KeyModifiers.HasFlag(KeyModifiers.Shift));
            Assert.Equal(expected.Meta, actual.KeyModifiers.HasFlag(KeyModifiers.Meta));
        }
    }

    private static void Pump(DesktopFixture f)
    { for (var i = 0; i < 6; i++) { f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
}
