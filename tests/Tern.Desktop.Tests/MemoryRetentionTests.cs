using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tern.App.Shell;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed partial class AIWritingWorkflowTests
{
    [AvaloniaFact]
    public async Task Closed_assistant_windows_and_workspaces_are_released_from_memory()
    {
        using var f = Fixture((_, _) => Task.FromResult(Models()));
        await f.WriteAsync("notes/a.md", "# A\n\nLaunch notes.");
        var folder = Path.Combine(f.Docs, "notes");
        var windows = new List<WeakReference>();
        var sessions = new List<WeakReference>();
        for (var round = 0; round < 5; round++)
        {
            windows.Add(OpenAndCloseAssistant(f));
            sessions.Add(await OpenAndCloseWorkspace(f, folder));
            Dispatcher.UIThread.RunJobs();
        }
        // The framework may briefly keep the most recent window for focus, so closed windows must not accumulate.
        await Released(windows);
        Assert.True(windows.Count(item => item.IsAlive) <= 1, $"{windows.Count(item => item.IsAlive)} of 5 closed assistant windows stayed in memory.");
        Assert.True(await Released(sessions), $"{sessions.Count(item => item.IsAlive)} of 5 closed workspaces stayed in memory.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndCloseAssistant(DesktopFixture f)
    {
        f.Window.ShowKnowledgeAssistant();
        var window = f.Window.OwnedWindows.OfType<KnowledgeAssistantWindow>().Single();
        Dispatcher.UIThread.RunJobs();
        var reference = new WeakReference(window.Assistant);
        window.Close();
        Dispatcher.UIThread.RunJobs();
        return reference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> OpenAndCloseWorkspace(DesktopFixture f, string folder)
    {
        Assert.True(await f.Shell.OpenFolderAsync(folder));
        var session = f.Shell.Workspace!;
        var reference = new WeakReference(session);
        Assert.True(await f.Shell.CloseWorkspaceAsync(session));
        return reference;
    }

    private static async Task<bool> Released(List<WeakReference> references)
    {
        for (var attempt = 0; attempt < 40 && references.Any(item => item.IsAlive); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        return references.All(item => !item.IsAlive);
    }
}
