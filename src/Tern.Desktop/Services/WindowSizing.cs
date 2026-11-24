using Avalonia.Controls;

namespace Tern.Desktop.Services;

/// <summary>Result of fitting the main window to a screen's working area (all values in device-independent pixels).</summary>
public readonly record struct WindowFit(double Width, double Height, double MinWidth, double MinHeight, bool Maximize);

/// <summary>Keeps the main window usable on small or high-DPI screens (S-UX-03, U-22).</summary>
public static class WindowSizing
{
    public const double DefaultWidth = 1440;
    public const double DefaultHeight = 880;
    public const double BaseMinWidth = 720;
    public const double BaseMinHeight = 420;
    private const double HorizontalMargin = 32;
    private const double VerticalMargin = 48;

    /// <summary>Below this effective width the Library collapses; below this height the chrome condenses.</summary>
    public const double LibraryCollapseWidth = 1000;
    public const double ShortHeight = 600;

    /// <summary>Scale the minimums by the UI scale but never beyond what the working area can show.</summary>
    public static (double MinWidth, double MinHeight) Minimums(double workWidth, double workHeight, double uiScale)
    {
        var scale = Math.Clamp(double.IsFinite(uiScale) ? uiScale : 1, 1, 2.5);
        return (Math.Min(BaseMinWidth * scale, Math.Max(1, workWidth)), Math.Min(BaseMinHeight * scale, Math.Max(1, workHeight)));
    }

    /// <summary>Clamps a requested size to the working area; <c>Maximize</c> is set when the request does not fit.</summary>
    public static WindowFit Fit(double workWidth, double workHeight, double uiScale, double desiredWidth, double desiredHeight)
    {
        var (minWidth, minHeight) = Minimums(workWidth, workHeight, uiScale);
        var maxWidth = Math.Max(minWidth, workWidth - HorizontalMargin);
        var maxHeight = Math.Max(minHeight, workHeight - VerticalMargin);
        var fits = desiredWidth <= maxWidth && desiredHeight <= maxHeight;
        return new WindowFit(
            Math.Clamp(desiredWidth, minWidth, maxWidth),
            Math.Clamp(desiredHeight, minHeight, maxHeight),
            minWidth, minHeight, !fits);
    }

    public static bool IsCompactWidth(double width, double uiScale) => width / Math.Max(1, uiScale) < LibraryCollapseWidth;

    public static bool IsShortHeight(double height) => height < ShortHeight;

    internal static (double Width, double Height)? WorkingAreaDips(Window window) =>
        window.Screens.ScreenFromWindow(window) is { } screen && screen.Scaling > 0
            ? (screen.WorkingArea.Width / screen.Scaling, screen.WorkingArea.Height / screen.Scaling)
            : null;

    /// <summary>Applies the scaled minimums and, when no layout was saved, fits the default size to the screen.</summary>
    internal static void ApplyInitial(Window window, double uiScale)
    {
        if (WorkingAreaDips(window) is not { } area) return;
        var fit = Fit(area.Width, area.Height, uiScale, DefaultWidth, DefaultHeight);
        window.MinWidth = fit.MinWidth;
        window.MinHeight = fit.MinHeight;
        window.Width = fit.Width;
        window.Height = fit.Height;
        if (fit.Maximize) window.WindowState = WindowState.Maximized;
    }

    /// <summary>Keeps a saved size inside the working area of the screen that now shows the window.</summary>
    internal static (double Width, double Height) ClampSaved(Window window, double uiScale, double width, double height)
    {
        if (WorkingAreaDips(window) is not { } area) return (width, height);
        var fit = Fit(area.Width, area.Height, uiScale, width, height);
        return (fit.Width, fit.Height);
    }
}
