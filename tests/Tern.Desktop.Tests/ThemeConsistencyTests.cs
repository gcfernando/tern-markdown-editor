using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Settings;
using Tern.Desktop.Themes;
using Tern.Desktop.Views;
using Tern.PreviewView;

namespace Tern.Desktop.Tests;

public sealed class ThemeConsistencyTests
{
    [AvaloniaFact]
    public void System_contrast_palette_overrides_user_theme_and_restores_it_without_changing_Windows()
    {
        using var f = new DesktopFixture();
        var previous = ThemeManager.ContrastPaletteProvider;
        var palette = new SystemContrastPalette(Colors.Black, Colors.Yellow, Colors.White, Colors.Purple, Colors.White, Colors.Cyan);
        try
        {
            ThemeManager.ContrastPaletteProvider = _ => palette;
            f.Shell.Settings.Theme = ThemeChoice.Paper;
            f.Window.ApplyTheme();
            Assert.Equal(ThemeVariant.Dark, f.Window.ActualThemeVariant);
            Assert.Equal(palette.Background, ResourceBrush(f.Window, ThemeManager.Bg).Color);
            Assert.Equal(palette.Text, ResourceBrush(f.Window, ThemeManager.Text).Color);
            Assert.Equal(palette.Border, ResourceBrush(f.Window, ThemeManager.ControlBorder).Color);
            Assert.Equal(palette.Highlight, ResourceBrush(f.Window, ThemeManager.Accent).Color);
            Assert.Equal(palette.Highlight, ResourceBrush(f.Window, PreviewKeys.FocusBackground).Color);
            Assert.Equal(palette.HighlightText, ResourceBrush(f.Window, PreviewKeys.FocusForeground).Color);
            ThemeManager.ContrastPaletteProvider = _ => null;
            f.Window.ApplyTheme();
            Assert.Equal(ThemeVariant.Light, f.Window.ActualThemeVariant);
            Assert.NotEqual(palette.Background, ResourceBrush(f.Window, ThemeManager.Bg).Color);
        }
        finally { ThemeManager.ContrastPaletteProvider = previous; f.Window.ApplyTheme(); }
    }

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper)]
    [InlineData(ThemeChoice.Midnight)]
    public void Referenced_XAML_and_theme_color_resources_resolve_in_both_themes(ThemeChoice theme)
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.Theme = theme;
        f.Window.ApplyTheme();
        var source = Path.Combine(Screenshots.RepositoryRoot, "src", "Tern.Desktop");
        var keys = typeof(ThemeManager).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(source, "*.axaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(file), @"\{(?:DynamicResource|StaticResource)\s+(Tern\.[\w.]+)\}"))
                keys.Add(match.Groups[1].Value);
        }
        foreach (var key in keys)
            Assert.True(f.Window.TryFindResource(key, f.Window.ActualThemeVariant, out var value) && value is not null,
                $"{theme}: missing referenced resource {key}");
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Text_and_interactive_boundaries_meet_contrast_on_each_chrome_surface(bool dark)
    {
        var resources = dark ? ThemeManager.Midnight() : ThemeManager.Paper();
        string[] textKeys = [ThemeManager.Text, ThemeManager.Muted, ThemeManager.Accent, ThemeManager.Success,
            ThemeManager.Danger, ThemeManager.Warning, ThemeManager.SyntaxHeading, ThemeManager.SyntaxEmphasis,
            ThemeManager.SyntaxCode, ThemeManager.SyntaxLink, ThemeManager.SyntaxQuote, ThemeManager.SyntaxMarker, ThemeManager.SyntaxMeta];
        foreach (var background in new[] { ThemeManager.Panel, ThemeManager.Surface, ThemeManager.Hover })
        {
            foreach (var foreground in textKeys)
                Assert.True(Contrast(ColorOf(resources, foreground), ColorOf(resources, background)) >= 4.5,
                    $"{(dark ? "Midnight" : "Paper")} {foreground} on {background} must reach 4.5:1.");
            Assert.True(Contrast(ColorOf(resources, ThemeManager.ControlBorder), ColorOf(resources, background)) >= 3);
        }
    }

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Graphite, true)]
    [InlineData(ThemeChoice.Harbor, true)]
    [InlineData(ThemeChoice.Linen, false)]
    [InlineData(ThemeChoice.Mist, false)]
    public void Added_themes_apply_their_palette_and_meet_contrast(ThemeChoice theme, bool dark)
    {
        var resources = theme switch
        {
            ThemeChoice.Graphite => ThemeManager.Graphite(),
            ThemeChoice.Harbor => ThemeManager.Harbor(),
            ThemeChoice.Linen => ThemeManager.Linen(),
            _ => ThemeManager.Mist(),
        };
        string[] textKeys = [ThemeManager.Text, ThemeManager.Muted, ThemeManager.Accent, ThemeManager.Success,
            ThemeManager.Danger, ThemeManager.Warning, ThemeManager.SyntaxHeading, ThemeManager.SyntaxEmphasis,
            ThemeManager.SyntaxCode, ThemeManager.SyntaxLink, ThemeManager.SyntaxQuote, ThemeManager.SyntaxMarker, ThemeManager.SyntaxMeta];
        foreach (var background in new[] { ThemeManager.Panel, ThemeManager.Surface, ThemeManager.Hover })
        {
            foreach (var foreground in textKeys)
                Assert.True(Contrast(ColorOf(resources, foreground), ColorOf(resources, background)) >= 4.5,
                    $"{theme} {foreground} on {background} must reach 4.5:1.");
            Assert.True(Contrast(ColorOf(resources, ThemeManager.ControlBorder), ColorOf(resources, background)) >= 3);
        }
        Assert.True(Contrast(ColorOf(resources, ThemeManager.OnAccent), ColorOf(resources, ThemeManager.Accent)) >= 4.5,
            $"{theme} accent button text must reach 4.5:1.");
        using var f = new DesktopFixture();
        f.Shell.Settings.Theme = theme;
        f.Window.ApplyTheme();
        Assert.Equal(dark ? ThemeVariant.Dark : ThemeVariant.Light, f.Window.ActualThemeVariant);
        Assert.Equal(ColorOf(resources, ThemeManager.Surface), ResourceBrush(f.Window, ThemeManager.Surface).Color);
    }

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper)]
    [InlineData(ThemeChoice.Midnight)]
    public void Fluent_accents_and_shared_find_palette_resolve_from_the_active_theme(ThemeChoice theme)
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.Theme = theme;
        f.Window.ApplyTheme();
        var accent = ResourceBrush(f.Window, ThemeManager.Accent).Color;
        foreach (var suffix in new[] { "", "Dark1", "Dark2", "Dark3", "Light1", "Light2", "Light3" })
        {
            Assert.True(f.Window.TryFindResource("SystemAccentColor" + suffix, f.Window.ActualThemeVariant, out var value));
            Assert.IsType<Color>(value);
        }
        Assert.True(f.Window.TryFindResource("SystemAccentColor", f.Window.ActualThemeVariant, out var primary));
        Assert.Equal(accent, Assert.IsType<Color>(primary));
        foreach (var key in new[] { "Tern.FindMatch", "Tern.FindMatchOther", "Tern.FindMatchOutline",
                     "Tern.Scrim", "Tern.CanvasBackground" })
            Assert.IsAssignableFrom<IBrush>(ResourceBrush(f.Window, key));
    }

    [AvaloniaFact]
    public void Open_dialog_text_and_line_heights_rescale_live_without_changing_document_fonts()
    {
        using var f = new DesktopFixture();
        var url = new OpenUrlWindow();
        var about = new AboutWindow();
        try
        {
            url.Show(f.Window); about.Show(f.Window); Pump(f.Window);
            var input = Assert.Single(url.GetVisualDescendants().OfType<TextBox>());
            var body = Assert.Single(about.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text?.StartsWith("Write and read Markdown files", StringComparison.Ordinal) == true);
            var caption = Assert.Single(about.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text?.StartsWith("Version ", StringComparison.Ordinal) == true);
            var editorSize = f.Window.Editor.FontSize;
            var previewSize = f.Window.PreviewControl.FontSize;
            foreach (var scale in new[] { 1.5, .75, 2.0, 1.0 })
            {
                f.Shell.Settings.UiScale = scale;
                f.Window.ApplySettings(); Pump(about); Pump(url);
                var effective = Math.Max(11.0 / 12, scale);
                Assert.Equal(14 * effective, input.FontSize, 6);
                Assert.Equal(14 * effective, body.FontSize, 6);
                Assert.Equal(21 * effective, body.LineHeight, 6);
                Assert.Equal(12 * effective, caption.FontSize, 6);
                Assert.True(caption.FontSize >= 11);
            }
            Assert.Equal(editorSize, f.Window.Editor.FontSize);
            Assert.Equal(previewSize, f.Window.PreviewControl.FontSize);
        }
        finally { about.Close(); url.Close(); }
    }

    [AvaloniaFact]
    public void Applying_the_settings_scale_updates_another_open_dialog_but_staged_edits_do_not()
    {
        using var f = new DesktopFixture();
        var about = new AboutWindow();
        var settings = new SettingsWindow(f.Shell.Settings, commit: f.Window.ApplySettings);
        try
        {
            about.Show(f.Window); settings.Show(f.Window); Pump(settings);
            var body = Assert.Single(about.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text?.StartsWith("Write and read Markdown files", StringComparison.Ordinal) == true);
            var scale = Assert.Single(settings.GetVisualDescendants().OfType<NumericUpDown>(),
                control => AutomationProperties.GetName(control) == "Interface text scale");
            scale.Value = 1.5m; Pump(settings);
            Assert.Equal(14, body.FontSize);
            Assert.Equal(1, f.Shell.Settings.UiScale);
            Assert.Single(settings.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Apply"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(settings); Pump(about);
            Assert.Equal(21, body.FontSize);
            Assert.Equal(1.5, f.Shell.Settings.UiScale);
            Assert.True(settings.IsVisible);
        }
        finally { settings.Close(); about.Close(); }
    }

    [AvaloniaFact]
    public void Loading_legacy_tiny_interface_scales_keeps_captions_readable()
    {
        using var f = new DesktopFixture();
        var store = new SettingsStore(Path.Combine(f.AppData, "legacy-small-type"));
        var settings = new AppSettings { UiScale = .75 };
        store.Save(settings);
        var restored = store.Load();
        Assert.Equal(11.0 / 12, restored.UiScale);
        f.Shell.Settings.UiScale = restored.UiScale;
        f.Window.ApplySettings();
        Assert.Equal(11, f.Window.FindControl<TextBlock>("WorkspaceCount")!.FontSize);
    }

    [AvaloniaFact]
    public void System_theme_changes_update_chrome_editor_and_preview_in_the_same_dispatch()
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("# Heading\n\nA paragraph");
        f.Shell.Settings.Theme = ThemeChoice.System;
        f.Window.ApplyTheme();
        var app = Application.Current!;
        try
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Light })
            {
                var clock = Stopwatch.StartNew();
                app.RequestedThemeVariant = variant;
                Pump(f.Window);
                var expected = variant == ThemeVariant.Dark ? ThemeManager.Midnight() : ThemeManager.Paper();
                Assert.Equal(ColorOf(expected, ThemeManager.Bg), Assert.IsAssignableFrom<ISolidColorBrush>(f.Window.Background).Color);
                Assert.Equal(ColorOf(expected, ThemeManager.LineHighlight),
                    Assert.IsAssignableFrom<ISolidColorBrush>(f.Window.Editor.TextArea.TextView.CurrentLineBackground).Color);
                Assert.Equal(ColorOf(expected, PreviewKeys.Background), ResourceBrush(f.Window.PreviewControl, PreviewKeys.Background).Color);
                Assert.Equal(ThemeChoice.System, f.Shell.Settings.Theme);
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
            }
        }
        finally { app.RequestedThemeVariant = ThemeVariant.Default; }
    }

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper)]
    [InlineData(ThemeChoice.Midnight)]
    public void Splitters_have_wide_hit_targets_and_a_contrasting_hover_and_keyboard_grip(ThemeChoice theme)
    {
        using var f = new DesktopFixture();
        f.Shell.NewDocument("Text");
        f.Shell.Settings.Theme = theme; f.Window.ApplyTheme(); Pump(f.Window);
        var splitters = f.Window.GetVisualDescendants().OfType<GridSplitter>().Where(x => x.IsEffectivelyVisible).ToArray();
        Assert.NotEmpty(splitters);
        foreach (var splitter in splitters)
        {
            Assert.True(splitter.Bounds.Width >= 6);
            var point = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), f.Window)!.Value;
            f.Window.MouseMove(point); Pump(f.Window);
            var grip = Assert.Single(splitter.GetVisualDescendants().OfType<Border>(), b => b.Name == "TernSplitterGrip");
            Assert.Equal(ResourceBrush(splitter, ThemeManager.ControlBorder).Color, Assert.IsAssignableFrom<ISolidColorBrush>(grip.Background).Color);
            Assert.True(grip.Width >= 3);
            f.Window.MouseMove(default); Assert.True(splitter.Focus(NavigationMethod.Tab)); Pump(f.Window);
            Assert.Equal(ResourceBrush(splitter, ThemeManager.ControlBorder).Color, Assert.IsAssignableFrom<ISolidColorBrush>(grip.Background).Color);
        }
    }

    private static Color ColorOf(ResourceDictionary resources, string key) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(resources[key]).Color;

    private static ISolidColorBrush ResourceBrush(Control control, string key)
    {
        Assert.True(control.TryFindResource(key, control.ActualThemeVariant, out var value), key);
        return Assert.IsAssignableFrom<ISolidColorBrush>(value);
    }

    private static double Contrast(Color a, Color b)
    {
        static double Linear(byte value) => value / 255.0 <= .04045 ? value / 255.0 / 12.92 : Math.Pow((value / 255.0 + .055) / 1.055, 2.4);
        static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        var x = Luminance(a); var y = Luminance(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }

    private static void Pump(Control control) { Dispatcher.UIThread.RunJobs(); control.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
}
