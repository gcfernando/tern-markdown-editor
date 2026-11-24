using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class ShortcutsViewportTests
{
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Compact_commands_and_editor_have_real_viewports_and_can_be_reached(double scale)
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.UiScale = scale;
        fixture.Window.ApplySettings();
        var window = new ShortcutsWindow(fixture.Shell.Registry) { Width = 700, Height = 650 };
        try
        {
            window.Show(fixture.Window); Pump(window);
            var commands = window.Commands;
            var editor = Assert.Single(window.GetVisualDescendants().OfType<TextBox>(),
                control => AutomationProperties.GetName(control) == "New shortcut");
            var body = Assert.IsType<Grid>(commands.Parent);
            Assert.Single(body.ColumnDefinitions);
            Assert.Equal(1, Grid.GetRow(commands));
            Assert.True(commands.Bounds.Height >= 100, $"The commands viewport is {commands.Bounds.Height} at {scale:P0}.");
            Assert.True(commands.Bounds.Width > 100);
            commands.ScrollIntoView(commands.Items[^1]); Pump(window);
            Assert.NotNull(commands.ContainerFromIndex(commands.ItemCount - 1));
            editor.BringIntoView(); Pump(window);
            Assert.True(editor.IsEffectivelyVisible);
            Assert.True(editor.Bounds.Width >= 100);
            var origin = editor.TranslatePoint(default, window)!.Value;
            Assert.True(origin.Y >= 0 && origin.Y + editor.Bounds.Height <= window.Bounds.Height,
                $"The shortcut editor is clipped at {scale:P0}: {origin.Y}, height {editor.Bounds.Height}, window {window.Bounds.Height}.");
        }
        finally { window.Close(); fixture.Shell.Settings.UiScale = 1; fixture.Window.ApplySettings(); }
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Categories_fit_and_remain_reachable_at_minimum_size(double scale)
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.UiScale = scale;
        fixture.Window.ApplySettings();
        var window = new ShortcutsWindow(fixture.Shell.Registry);
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        try
        {
            window.Show(fixture.Window); Pump(window);
            var categories = Assert.Single(window.GetVisualDescendants().OfType<ListBox>(),
                c => AutomationProperties.GetName(c) == "Shortcut categories");
            var close = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                b => AutomationProperties.GetName(b) == "Close shortcuts");
            var closeOrigin = close.TranslatePoint(default, window)!.Value;
            Assert.InRange(closeOrigin.Y + close.Bounds.Height, 1, window.Bounds.Height);
            Assert.True(categories.Bounds.Height >= 80, "The category list needs a usable viewport.");
            foreach (var category in categories.Items.OfType<string>().ToArray())
            {
                categories.ScrollIntoView(category); Pump(window);
                var container = Assert.IsAssignableFrom<ListBoxItem>(categories.ContainerFromItem(category));
                var label = Assert.Single(container.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == category);
                Assert.DoesNotContain(label.TextLayout.TextLines, line => line.HasCollapsed);
                Assert.All(label.TextLayout.TextLines, line => Assert.True(line.Width <= label.Bounds.Width + 1,
                    $"{category} clips at {scale:P0}: {line.Width} > {label.Bounds.Width}"));
                var origin = container.TranslatePoint(default, categories)!.Value;
                Assert.True(origin.Y < categories.Bounds.Height && origin.Y + container.Bounds.Height > 0,
                    $"{category} cannot be reached by scrolling.");
            }
        }
        finally
        {
            window.Close(); fixture.Shell.Settings.UiScale = 1; fixture.Window.ApplySettings();
        }
    }

    private static void Pump(Window window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
}
