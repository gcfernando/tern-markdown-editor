using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Help;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class SettingsViewportTests
{
    [AvaloniaTheory]
    [InlineData(1.0, true)]
    [InlineData(1.5, true)]
    [InlineData(2.0, true)]
    [InlineData(1.0, false)]
    [InlineData(1.5, false)]
    [InlineData(2.0, false)]
    public void Categories_and_settings_stay_inside_viewports_above_fixed_footer(double scale, bool minimum)
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.Settings.UiScale = scale;
        fixture.Window.ApplySettings();
        var window = new SettingsWindow(fixture.Shell.Settings, openFeature: _ => Task.CompletedTask);
        window.Width = minimum ? window.MinWidth : 1600;
        window.Height = window.MinHeight;
        window.Show(fixture.Window);
        Pump(window);
        var scrolls = window.GetVisualDescendants().OfType<ScrollViewer>().ToArray();
        var content = Assert.Single(scrolls, s => AutomationProperties.GetName(s) == "Settings content viewport");
        var sidebar = Assert.Single(scrolls, s => AutomationProperties.GetName(s) == "Settings category viewport");
        var choice = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(), c => AutomationProperties.GetName(c) == "Choose settings category");
        var done = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "Close settings");
        var doneTop = done.TranslatePoint(default, window)!.Value.Y;
        Assert.True(content.Viewport.Width > 300);
        Assert.True(content.Viewport.Height > 60);
        Assert.True(content.TranslatePoint(default, window)!.Value.Y + content.Bounds.Height <= doneTop);
        foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => AutomationProperties.GetName(b) is "Close settings" or "Configure AI connections"))
        {
            var point = button.TranslatePoint(default, window)!.Value;
            Assert.InRange(point.X, 0, window.ClientSize.Width - button.Bounds.Width);
            Assert.InRange(point.Y, 0, window.ClientSize.Height - button.Bounds.Height);
        }
        var navigation = Assert.Single(window.GetVisualDescendants().OfType<ListBox>(), l => AutomationProperties.GetName(l) == "Settings categories");
        Assert.Equal(FeatureCatalog.SettingsFeatures.Select(f => f.Category).Append("AI connections").Distinct(StringComparer.Ordinal).Count(), navigation.ItemCount);
        if (minimum)
        {
            Assert.True(choice.IsEffectivelyVisible);
            Assert.False(sidebar.IsVisible);
            Assert.True(choice.Bounds.Width > 300);
            choice.SelectedIndex = choice.ItemCount - 1;
            Assert.Equal(choice.SelectedIndex, navigation.SelectedIndex);
        }
        else
        {
            Assert.True(sidebar.IsVisible);
            Assert.True(sidebar.Viewport.Width > 150 && sidebar.Viewport.Height > 60);
            Assert.True(sidebar.TranslatePoint(default, window)!.Value.Y + sidebar.Bounds.Height <= doneTop);
            var selectedContainer = Assert.IsType<ListBoxItem>(navigation.ContainerFromIndex(0));
            Assert.True(selectedContainer.Focus());
            Assert.True(selectedContainer.IsFocused);
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Pump(window);
            Assert.Equal(1, navigation.SelectedIndex);
            sidebar.Offset = new Vector(0, sidebar.Extent.Height);
            Pump(window);
            // A content-fitted dialog can show the complete short category list.
            // Require scrolling only when it actually exceeds the viewport.
            if (sidebar.Extent.Height > sidebar.Viewport.Height + 1)
                Assert.True(sidebar.Offset.Y > 0);
            else
                Assert.InRange(sidebar.Offset.Y, 0, 1);
            var finalContainer = navigation.ContainerFromIndex(navigation.ItemCount - 1)!;
            finalContainer.BringIntoView();
            Pump(window);
            var point = finalContainer.TranslatePoint(default, sidebar)!.Value;
            Assert.True(point.Y < sidebar.Viewport.Height && point.Y + finalContainer.Bounds.Height > 0);
        }
        var search = Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), t => AutomationProperties.GetName(t) == "Find a setting");
        search.Text = "wrap";
        Pump(window);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "1 setting · Changes apply when Settings closes");
        var folder = Path.Combine(Screenshots.Folder, "settings-viewport");
        Directory.CreateDirectory(folder);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(folder, $"settings-{scale:0.0}-{(minimum ? "minimum" : "wide")}.png"));
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        window.Close();
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
