using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Settings;
using Tern.Desktop.Themes;
using Tern.Desktop.Views;
using Tern.PreviewView;

namespace Tern.Desktop.Tests;

public sealed class ScrollbarPresentationTests
{
    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper)]
    [InlineData(ThemeChoice.Midnight)]
    public async Task Source_reading_nested_code_and_settings_share_visible_rounded_grips_and_native_states(ThemeChoice theme)
    {
        using var f = new DesktopFixture();
        var original = await PrepareDocument(f, theme);
        var bars = MainBars(f);
        foreach (var bar in bars) AssertGrip(bar);
        Assert.Equal(BrushColor(bars[0], PreviewKeys.Muted), GripColor(bars[0]));
        Assert.All(bars, bar => Assert.Equal(GripColor(bars[0]), GripColor(bar)));
        var outline = f.Window.FindControl<ListBox>("OutlineList")!;
        var outlineViewer = OutlineScroller(f);
        Assert.True(outlineViewer.Extent.Height > outlineViewer.Viewport.Height + 20);
        var outlineSelection = outline.SelectedItem;
        var outlineCount = outline.Items.Count;
        var outlineOffset = outlineViewer.Offset.Y;
        var outlinePoint = outline.TranslatePoint(new Point(50, Math.Min(80, outline.Bounds.Height / 2)), f.Window)!.Value;
        f.Window.MouseWheel(outlinePoint, new Vector(0, -2)); Pump(f.Window);
        Assert.True(outlineViewer.Offset.Y > outlineOffset, "The real Outline list must respond to wheel input in its own units.");
        Assert.Equal(outlineSelection, outline.SelectedItem);
        Assert.Equal(outlineCount, outline.Items.Count);
        Assert.InRange(outline.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, Math.Min(80, outlineCount - 1));
        await Capture(f.Window, $"presentation-{theme}-normal");

        var sourceBar = bars[0];
        var thumb = ThumbOf(sourceBar);
        var point = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), f.Window)!.Value;
        f.Window.MouseMove(point); Pump(f.Window);
        Assert.Equal(BrushColor(sourceBar, PreviewKeys.Foreground), GripColor(sourceBar));
        await Capture(f.Window, $"presentation-{theme}-hover");
        f.Window.MouseDown(point, MouseButton.Left); Pump(f.Window);
        try
        {
            Assert.True(thumb.IsPointerOver);
            Assert.Equal(BrushColor(sourceBar, PreviewKeys.Foreground), GripColor(sourceBar));
            await Capture(f.Window, $"presentation-{theme}-pressed");
        }
        finally { f.Window.MouseUp(point, MouseButton.Left); Pump(f.Window); }

        var settings = new SettingsWindow(f.Shell.Settings, f.Shell.Registry) { Width = 1100, Height = 620 };
        settings.Show(f.Window);
        try
        {
            Pump(settings);
            var sidebar = Named<ScrollViewer>(settings, "Settings category viewport");
            var content = Named<ScrollViewer>(settings, "Settings content viewport");
            var navigation = Named<ListBox>(settings, "Settings categories");
            Assert.True(sidebar.IsEffectivelyVisible);
            Assert.True(sidebar.Extent.Height > sidebar.Viewport.Height + 20);
            foreach (var viewer in new[] { sidebar, content }) AssertGrip(BarOf(viewer, Orientation.Vertical));
            var selected = navigation.SelectedItem;
            var oldOffset = sidebar.Offset.Y;
            // Hit the visible category rows, not an artificial empty outer margin.
            var wheelPoint = sidebar.TranslatePoint(new Point(50, Math.Min(80, sidebar.Bounds.Height / 2)), settings)!.Value;
            settings.MouseWheel(wheelPoint, new Vector(0, -2)); Pump(settings);
            Assert.True(sidebar.Offset.Y > oldOffset, "Wheel over the actual category list must scroll its viewport.");
            Assert.Equal(selected, navigation.SelectedItem);
            Assert.Equal(theme, f.Shell.Settings.Theme);
            await Capture(settings, $"presentation-{theme}-settings");
        }
        finally { settings.Close(); Pump(f.Window); }
        Assert.Equal(original, f.Shell.ActiveTab!.CurrentText);
        Assert.False(f.Shell.ActiveTab.IsDirty);
    }

    [AvaloniaFact]
    public async Task Shared_grips_follow_the_synthetic_system_contrast_palette_without_changing_windows()
    {
        using var f = new DesktopFixture();
        var previous = ThemeManager.ContrastPaletteProvider;
        var palette = new SystemContrastPalette(Colors.Black, Colors.Yellow, Colors.White, Colors.Purple, Colors.White, Colors.Cyan);
        try
        {
            ThemeManager.ContrastPaletteProvider = _ => palette;
            var original = await PrepareDocument(f, ThemeChoice.Paper);
            foreach (var bar in MainBars(f))
            {
                AssertGrip(bar);
                Assert.Equal(palette.Text, GripColor(bar));
                Assert.Equal(palette.Background, Assert.IsAssignableFrom<ISolidColorBrush>(bar.Background).Color);
            }
            await Capture(f.Window, "presentation-HighContrast-normal");
            Assert.Equal(original, f.Shell.ActiveTab!.CurrentText);
            Assert.False(f.Shell.ActiveTab.IsDirty);
        }
        finally { ThemeManager.ContrastPaletteProvider = previous; f.Window.ApplyTheme(); }
    }

    private static async Task<string> PrepareDocument(DesktopFixture f, ThemeChoice theme)
    {
        var original = "# Shared scrollbars\n\n```csharp\nvar wide = \"" + new string('x', 280) + "\";\n```\n\n"
            + string.Join("\n\n", Enumerable.Range(0, 180).Select(index => $"## Section {index}\n\nParagraph with enough content to scroll."));
        var path = await f.WriteAsync("scrollbar-presentation.md", original);
        Assert.NotNull(await f.Shell.OpenFileAsync(path));
        f.Shell.Settings.Theme = theme;
        f.Shell.Settings.ReduceMotion = true;
        f.Shell.Settings.WordWrap = false;
        f.Shell.Settings.SyncScroll = false;
        f.Window.ApplySettings(); f.Window.ApplyTheme();
        await f.Window.RunCommandAsync(CommandIds.ViewSplit);
        await f.Window.RebuildPreviewAsync();
        for (var attempt = 0; attempt < 150 && f.Window.FindControl<ListBox>("OutlineList")!.Items.Count < 20; attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken); Pump(f.Window);
        }
        Assert.True(f.Window.FindControl<ListBox>("OutlineList")!.Items.Count >= 20, "Wait for actual asynchronous outline analysis before testing its scrollbar.");
        f.Window.MouseMove(new Point(-10, -10)); Pump(f.Window);
        return original;
    }

    private static ScrollBar[] MainBars(DesktopFixture f)
    {
        var source = f.Window.EditorControl.GetVisualDescendants().OfType<ScrollViewer>().First();
        var reading = f.Window.PreviewControl.GetVisualDescendants().OfType<ScrollViewer>().First();
        var code = f.Window.PreviewControl.GetVisualDescendants().OfType<ScrollViewer>().Single(viewer =>
            !ReferenceEquals(viewer, reading) && viewer.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto
            && viewer.Extent.Width > viewer.Viewport.Width + 10);
        return [BarOf(source, Orientation.Vertical), BarOf(source, Orientation.Horizontal),
            BarOf(reading, Orientation.Vertical), BarOf(code, Orientation.Horizontal), BarOf(OutlineScroller(f), Orientation.Vertical)];
    }

    private static ScrollViewer OutlineScroller(DesktopFixture f) => Assert.Single(f.Window.FindControl<ListBox>("OutlineList")!
        .GetVisualDescendants().OfType<ScrollViewer>());

    private static ScrollBar BarOf(ScrollViewer viewer, Orientation orientation) =>
        viewer.GetVisualDescendants().OfType<ScrollBar>().Single(bar => bar.Orientation == orientation
            && ReferenceEquals(bar.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault(), viewer));

    private static Thumb ThumbOf(ScrollBar bar) => Assert.Single(bar.GetVisualDescendants().OfType<Thumb>());

    private static Border GripOf(ScrollBar bar) => Assert.Single(ThumbOf(bar).GetVisualDescendants().OfType<Border>()
        .Where(border => border.IsEffectivelyVisible && border.Background is ISolidColorBrush));

    private static Color GripColor(ScrollBar bar) => Assert.IsAssignableFrom<ISolidColorBrush>(GripOf(bar).Background).Color;

    private static void AssertGrip(ScrollBar bar)
    {
        Assert.True(bar.IsEffectivelyVisible);
        Assert.False(bar.AllowAutoHide);
        var vertical = bar.Orientation == Orientation.Vertical;
        var thumb = ThumbOf(bar);
        var grip = GripOf(bar);
        Assert.InRange(vertical ? bar.Bounds.Width : bar.Bounds.Height, 20, 22);
        Assert.True((vertical ? thumb.Bounds.Height : thumb.Bounds.Width) >= 39.9);
        Assert.Equal(new CornerRadius(5), grip.CornerRadius);
        Assert.InRange(vertical ? grip.Bounds.Width : grip.Bounds.Height, 10, 18);
        Assert.True((vertical ? thumb.Bounds.Width - grip.Bounds.Width : thumb.Bounds.Height - grip.Bounds.Height) >= 4);
        var track = Assert.IsAssignableFrom<ISolidColorBrush>(bar.Background).Color;
        var fill = GripColor(bar);
        var opacity = fill.A / 255.0;
        foreach (var visual in grip.GetSelfAndVisualAncestors())
        {
            opacity *= visual.Opacity;
            if (ReferenceEquals(visual, bar)) break;
        }
        byte Blend(byte foreground, byte background) => (byte)Math.Round(foreground * opacity + background * (1 - opacity));
        var rendered = Color.FromRgb(Blend(fill.R, track.R), Blend(fill.G, track.G), Blend(fill.B, track.B));
        Assert.True(Contrast(rendered, track) >= 3, $"Actual grip fill must contrast with its track: {rendered} on {track}.");
    }

    private static Color BrushColor(Control control, string key)
    {
        Assert.True(control.TryFindResource(key, control.ActualThemeVariant, out var value));
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static T Named<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>()
        .Single(control => AutomationProperties.GetName(control) == name);

    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte value) => value / 255.0 <= .04045 ? value / 255.0 / 12.92 : Math.Pow((value / 255.0 + .055) / 1.055, 2.4);
        static double Luminance(Color value) => .2126 * Linear(value.R) + .7152 * Linear(value.G) + .0722 * Linear(value.B);
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static async Task Capture(Window window, string name)
    {
        await Task.Delay(50, TestContext.Current.CancellationToken); Pump(window);
        var folder = Path.Combine(Screenshots.Folder, "reported-scrolling");
        Directory.CreateDirectory(folder);
        using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
        using var output = File.Create(Path.Combine(folder, name + ".png"));
        bitmap.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void Pump(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
}
