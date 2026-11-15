using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Tern.Desktop.Services;

/// <summary>Registers an explicitly chosen copy. Never changes .md's default or Windows UserChoice.</summary>
internal static partial class WindowsFileAssociations
{
    internal const string ProgId = "Tern.Markdown";
    internal static IReadOnlyDictionary<string, string> Registration(string executable)
    {
        executable = Path.GetFullPath(executable);
        if (executable.Contains('"') || executable.Any(char.IsControl)) throw new ArgumentException("Invalid executable path.", nameof(executable));
        var command = "\"" + executable + "\" \"%1\"";
        return new Dictionary<string, string>
        {
            [@"Software\Classes\Applications\Tern.exe|FriendlyAppName"] = "Tern",
            [@"Software\Classes\Applications\Tern.exe\SupportedTypes|.md"] = "",
            [@"Software\Classes\Applications\Tern.exe\shell\open\command|"] = command,
            [@"Software\Classes\.md\OpenWithProgids|Tern.Markdown"] = "",
            [@"Software\Classes\Tern.Markdown|"] = "Markdown document",
            [@"Software\Classes\Tern.Markdown\DefaultIcon|"] = "\"" + executable + "\",0",
            [@"Software\Classes\Tern.Markdown\shell\open\command|"] = command,
            [@"Software\Classes\SystemFileAssociations\.md\shell\Tern.Open|"] = "Open with Tern",
            [@"Software\Classes\SystemFileAssociations\.md\shell\Tern.Open|Icon"] = executable,
            [@"Software\Classes\SystemFileAssociations\.md\shell\Tern.Open|MultiSelectModel"] = "Single",
            [@"Software\Classes\SystemFileAssociations\.md\shell\Tern.Open\command|"] = command,
            [@"Software\Tern\Capabilities|ApplicationName"] = "Tern",
            [@"Software\Tern\Capabilities|ApplicationDescription"] = "Markdown editor",
            [@"Software\Tern\Capabilities\FileAssociations|.md"] = ProgId,
            [@"Software\RegisteredApplications|Tern"] = @"Software\Tern\Capabilities"
        };
    }
    internal static void RegisterCurrentCopy()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } path) throw new PlatformNotSupportedException();
        foreach (var (entry, value) in Registration(path))
        {
            var parts = entry.Split('|');
            using var key = Registry.CurrentUser.CreateSubKey(parts[0]);
            key.SetValue(parts[1], value, RegistryValueKind.String);
        }
        SHChangeNotify(0x08000000, 0, 0, 0);
    }
    internal static string CurrentRegistration()
    {
        if (!OperatingSystem.IsWindows()) return "Windows Explorer integration is available on Windows.";
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Tern.Markdown\shell\open\command");
        return key?.GetValue("") as string ?? "This user has no registered copy of Tern.";
    }
    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
