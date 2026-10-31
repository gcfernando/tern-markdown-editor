using System.Diagnostics;
using Tern.App.Shell;

namespace Tern.Desktop.Services;

/// <summary>Hands links and files to the operating system. The shell has already asked the user before an external URL reaches here.</summary>
public sealed class SystemLauncher : IExternalLauncher
{
    public void OpenUri(Uri uri) => Start(uri.AbsoluteUri);

    public void RevealInFileManager(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Start("explorer.exe", $"/select,\"{path}\"");
        }
        else if (OperatingSystem.IsMacOS())
        {
            Start("open", $"-R \"{path}\"");
        }
        else
        {
            Start("xdg-open", $"\"{Path.GetDirectoryName(path)}\"");
        }
    }

    private static void Start(string target, string? arguments = null)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target, arguments ?? string.Empty) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Trace.TraceWarning("External launch failed ({0}).", ex.GetType().Name);
        }
    }
}
