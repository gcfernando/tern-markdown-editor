using Avalonia;
using Avalonia.Controls;
using Tern.App.Shell;
using Tern.Desktop.Views;
using Tern.Recovery;
using Tern.Workspaces;

namespace Tern.Desktop.Services;

/// <summary>What happens after the window first appears: restore the last session, open files from the command line, offer crash recovery.</summary>
public static class Startup
{
    public static async Task RunAsync(MainWindow window, ShellViewModel shell, IReadOnlyList<string> args)
    {
        var layout = await shell.RestoreSessionAsync(() => StartupMeasurements.Mark(shell.ActiveTab is null ? "session-restored-without-active-document" : "active-document-attached"));
        if (!window.IsVisible) return;
        ApplyLayout(window, layout);

        foreach (var arg in args.Where(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith('-')))
        {
            if (!window.IsVisible) return;
            var full = Path.GetFullPath(arg);
            if (Directory.Exists(full))
            {
                await shell.OpenDirectoryAsync(full);
            }
            else if (File.Exists(full))
            {
                await shell.OpenFileAsync(full);
            }
        }

        await OfferRecoveryAsync(window, shell);
        if (!window.IsVisible) return;
        if (shell.Tabs.Count == 0)
        {
            shell.NewStartupDocument();
        }
    }

    private static void ApplyLayout(Window window, WindowLayoutHint? layout)
    {
        if (layout is null)
        {
            return;
        }

        if (layout.Width is { } width && layout.Height is { } height && width >= window.MinWidth && height >= window.MinHeight)
        {
            window.Width = width;
            window.Height = height;
        }

        if (layout.Left is { } left && layout.Top is { } top && IsOnAnyScreen(window, (int)left, (int)top))
        {
            window.Position = new PixelPoint((int)left, (int)top);
        }

        if (layout.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    // A saved position can belong to a monitor that is no longer attached.
    private static bool IsOnAnyScreen(Window window, int x, int y) =>
        window.Screens.All.Any(s => s.Bounds.Contains(new PixelPoint(x + 20, y + 20)));

    public static async Task OfferRecoveryAsync(Window owner, ShellViewModel shell)
    {
        var scan = shell.FindRecoverable();
        var entries = scan.Entries;
        if (scan.Corrupt.Count > 0)
            await PromptWindow.AskAsync(owner, "Some recovery files could not be read",
                $"{scan.Corrupt.Count} recovery file(s) failed integrity checks. They have been preserved for inspection; no backing document was changed.", ["Continue"], 0, 0);
        if (entries.Count == 0)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (!owner.IsVisible) return;
            string disk;
            try
            {
                disk = entry.Record.OriginalPath is { } path && File.Exists(path)
                    ? (await new Tern.Filesystem.DocumentFileService().LoadAsync(path)).Text
                    : "The original backing file is unavailable.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { disk = "The backing file could not be read: " + ex.Message; }
            var answer = await new RecoveryReviewWindow(entry.Record, disk).ShowDialog<int?>(owner) ?? 3;
            if (!owner.IsVisible) return;
            if (answer == 0)
            {
                await shell.RestoreRecoveryAsync(entry.Record);
            }
            else if (answer == 1)
            {
                var restored = await shell.RestoreRecoveryAsync(entry.Record);
                await shell.SaveTabAsAsync(restored);
            }
            else if (answer == 2)
            {
                shell.DiscardRecovery(entry.Record);
            }
        }
    }
}
