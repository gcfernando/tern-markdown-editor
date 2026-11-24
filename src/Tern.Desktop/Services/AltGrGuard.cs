using System.Runtime.InteropServices;
using Tern.App.Commands;

namespace Tern.Desktop.Services;

/// <summary>
/// Windows reports AltGr as Ctrl+Alt (a synthesized left Ctrl plus the right Alt key). A Ctrl+Alt+printable
/// registry gesture seen while AltGr is physically down is a typed character (ś, ², @, €, {), not a command.
/// </summary>
internal static partial class AltGrGuard
{
    private const int VkLeftControl = 0xA2;
    private const int VkRightMenu = 0xA5;

    internal static Func<int, short>? KeyStateOverride { get; set; }

    internal static bool IsAltGrDown() => IsAltGrDown(KeyStateOverride ?? DefaultKeyState);

    internal static bool IsAltGrDown(Func<int, short> keyState) =>
        keyState(VkRightMenu) < 0 && keyState(VkLeftControl) < 0;

    internal static bool ShouldIgnore(Gesture gesture) => gesture.CollidesWithAltGr && IsAltGrDown();

    private static short DefaultKeyState(int virtualKey) =>
        OperatingSystem.IsWindows() ? GetKeyState(virtualKey) : (short)0;

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int virtualKey);
}
