using Avalonia;
using Tern.App.Settings;

namespace Tern.Desktop.Themes;

internal static class ThemeMetrics
{
    public static void Apply(Application application, double requestedScale)
    {
        var scale = Math.Clamp(double.IsFinite(requestedScale) ? requestedScale : 1, AppSettings.MinimumUiScale, 2.5);
        var resources = application.Resources;
        foreach (var (name, size) in new[] { ("Caption", 12), ("Body", 14), ("Lead", 16), ("Section", 18), ("Title", 22) })
            resources["Tern.FontSize." + name] = size * scale;
        resources["Tern.LineHeight.Caption"] = 18 * scale;
        resources["Tern.LineHeight.Body"] = 21 * scale;
        resources["Tern.LineHeight.Lead"] = 24 * scale;
        foreach (var (name, radius) in new[] { ("Control", 6), ("Card", 8), ("Popup", 12) })
            resources["Tern.Radius." + name] = new CornerRadius(radius);
        resources["Tern.Radius.Tab"] = new CornerRadius(6, 6, 0, 0);
        resources["Tern.Inset.TabGap"] = new Thickness(0, 0, 4, 0);
        foreach (var space in new[] { 0, 4, 8, 12, 16, 24 })
        {
            resources["Tern.Space." + space] = (double)space;
            resources["Tern.Inset." + space] = new Thickness(space);
        }
        resources["Tern.UiFontSize"] = resources["Tern.FontSize.Body"];
        resources["Tern.SmallFontSize"] = resources["Tern.FontSize.Caption"];
        resources["Tern.TitleFontSize"] = resources["Tern.FontSize.Title"];
        resources["Tern.SectionFontSize"] = resources["Tern.FontSize.Section"];
        resources["Tern.MenuItemHeight"] = 24 * scale + 12;
    }
}
