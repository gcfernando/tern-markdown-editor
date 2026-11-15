using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.Desktop.Themes;

namespace Tern.Desktop.Tests;

public sealed class ShortcutsHoverTests
{
    [AvaloniaTheory]
    [InlineData(ThemeChoice.Midnight)]
    [InlineData(ThemeChoice.Paper)]
    public void Shortcuts_header_uses_the_app_palette_when_hovered_and_pressed(ThemeChoice theme)
    {
        using var f = new DesktopFixture();
        ThemeManager.Apply(Application.Current!, theme);
        Pump(f);
        var expander = Assert.Single(f.Window.GetVisualDescendants().OfType<Expander>(), e => e.Classes.Contains("library-shortcuts"));
        var header = Assert.Single(expander.GetVisualDescendants().OfType<ToggleButton>(), t => t.Name == "ExpanderHeader");
        var point = header.TranslatePoint(new Rect(header.Bounds.Size).Center, f.Window)!.Value;
        f.Window.MouseMove(point);
        Pump(f);
        Assert.True(header.IsPointerOver);
        CheckPalette(header);
        using (var frame = f.Window.CaptureRenderedFrame())
        {
            Assert.NotNull(frame);
            frame.Save(Path.Combine(Screenshots.Folder, $"shortcuts-hover-{theme}.png"));
        }
        f.Window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        Pump(f);
        CheckPalette(header);
        f.Window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        Pump(f);
        Assert.False(expander.IsExpanded);
    }

    private static void CheckPalette(Control header)
    {
        var backgrounds = header.GetVisualDescendants().OfType<Border>()
            .Select(b => (b.Name, b.Background)).Where(b => b.Background is ISolidColorBrush { Color.A: > 0 }).ToArray();
        Assert.DoesNotContain(backgrounds, b => b.Background is ISolidColorBrush brush && brush.Color == Colors.Black);
        Assert.Contains(backgrounds, b => b.Name == "ShortcutsHeaderSurface");
    }

    private static void Pump(DesktopFixture f)
    { for (var i = 0; i < 4; i++) { f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
}
