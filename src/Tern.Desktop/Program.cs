using Avalonia;
using System.Runtime.InteropServices;
using Tern.App.Shell;
using Tern.Desktop.Services;

namespace Tern.Desktop;

internal static class Program
{
    internal static SingleInstanceCoordinator? Instance { get; private set; }

    // Windows clipboard, native dialogs and Explorer/OLE drops require an STA UI thread.
    [STAThread]
    public static void Main(string[] args)
    {
        StartupMeasurements.Mark("managed-entry");
        try
        {
            using var instance = new SingleInstanceCoordinator(AppPaths.Default());
            if (!instance.IsPrimary)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                instance.ForwardAsync(args, timeout.Token).GetAwaiter().GetResult();
                return;
            }
            Instance = instance;
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException or NotSupportedException)
        {
            var message = "Tern could not start or forward the requested files to its running instance. No second editor was started. "
                + "Switch to the existing window, or close it and try again.\n\n" + ex.Message;
            if (OperatingSystem.IsWindows()) MessageBoxW(0, message, "Tern could not open", 0x10);
            else Console.Error.WriteLine(message);
            Environment.ExitCode = 1;
        }
        finally { Instance = null; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}
