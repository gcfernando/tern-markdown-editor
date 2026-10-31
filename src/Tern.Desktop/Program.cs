using Avalonia;
using Tern.Desktop.Services;

namespace Tern.Desktop;

internal static class Program
{
    // Windows clipboard, native dialogs and Explorer/OLE drops require an STA UI thread.
    [STAThread]
    public static void Main(string[] args)
    {
        StartupMeasurements.Mark("managed-entry");
        AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace()
        .StartWithClassicDesktopLifetime(args);
    }
}
