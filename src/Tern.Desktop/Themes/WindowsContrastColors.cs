using System.Runtime.InteropServices;
using Avalonia.Media;

namespace Tern.Desktop.Themes;

internal sealed record SystemContrastPalette(Color Background, Color Text, Color Border, Color Highlight, Color HighlightText, Color Link);

internal static class WindowsContrastColors
{
    public static SystemContrastPalette Read()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows contrast colours require Windows.");
        return new(Read(5), Read(8), Read(6), Read(13), Read(14), Read(26));
    }

    private static Color Read(int index)
    {
        var color = GetSysColor(index);
        return Color.FromRgb((byte)color, (byte)(color >> 8), (byte)(color >> 16));
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetSysColor(int index);
}
