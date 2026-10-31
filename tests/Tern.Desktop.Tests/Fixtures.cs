using Tern.App.Documents;
using Tern.App.Shell;
using Tern.Desktop.Services;
using Tern.Desktop.Views;
using Tern.Domain.Merging;

namespace Tern.Desktop.Tests;

/// <summary>Answers the prompts a test expects; file operations stay real.</summary>
internal sealed class ScriptedDialogs : IShellDialogs
{
    public DuplicateOpenChoice DuplicateChoice { get; set; } = DuplicateOpenChoice.Cancel;
    public int DuplicatePrompts { get; private set; }
    public string? SavePath { get; set; }
    public bool Overwrite { get; set; }
    public bool ConfirmDelete { get; set; }
    public int DeletePrompts { get; private set; }
    public Task<bool> ConfirmDeleteAsync(string path, int unsavedCount)
    { DeletePrompts++; return Task.FromResult(ConfirmDelete); }
    public Task<DuplicateOpenChoice> AskDuplicateOpenAsync(string documentName)
    { DuplicatePrompts++; return Task.FromResult(DuplicateChoice); }
    public SaveChoice SaveChoice { get; set; } = SaveChoice.Cancel;

    public int SavePrompts { get; private set; }

    public Task<SaveChoice> AskSaveChangesAsync(string documentName)
    {
        SavePrompts++;
        return Task.FromResult(SaveChoice);
    }

    public Task<ConflictChoice> AskConflictAsync(DocumentTab tab) => Task.FromResult(ConflictChoice.Cancel);

    public Task<MissingFileChoice> AskFileMissingAsync(DocumentTab tab) => Task.FromResult(MissingFileChoice.KeepEditing);

    public Task<string?> ResolveMergeAsync(DocumentTab tab, MergeResult merge) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> PickFilesToOpenAsync() => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickSavePathAsync(string suggestedName, string? initialDirectory) => Task.FromResult(SavePath);

    public Task<bool> ConfirmOverwriteAsync(string path) => Task.FromResult(Overwrite);

    public bool AllowExternal { get; set; }
    public List<Uri> ExternalAsked { get; } = [];
    public Task<bool> ConfirmOpenExternalAsync(Uri uri)
    {
        ExternalAsked.Add(uri);
        return Task.FromResult(AllowExternal);
    }

    public void ShowMessage(string title, string message)
    {
    }
}

internal sealed class NullLauncher : IExternalLauncher
{
    public List<Uri> Opened { get; } = [];
    public List<string> Revealed { get; } = [];
    public void OpenUri(Uri uri)
    {
        Opened.Add(uri);
    }

    public void RevealInFileManager(string path)
    {
        Revealed.Add(path);
    }
}

/// <summary>A real shell over a throw-away app-data folder and workspace, shown in a real (headless) window.</summary>
internal sealed class DesktopFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tern-desktop-tests", Guid.NewGuid().ToString("N"));

    public DesktopFixture(Func<string, Tern.Desktop.Services.AIWorkspaceService>? createAI = null)
    {
        Docs = Path.Combine(_root, "docs");
        Directory.CreateDirectory(Docs);
        Dialogs = new ScriptedDialogs();
        Launcher = new NullLauncher();
        Shell = new ShellViewModel(AppPaths.Under(Path.Combine(_root, "appdata")), Dialogs, Launcher);
        Window = new MainWindow(Shell, aiWorkspace: createAI?.Invoke(Shell.Paths.SettingsDirectory))
        {
            TableDimensionsPicker = () => Task.FromResult<TableDimensions?>(new(3, 3)),
            ImageImportChoicePicker = _ => Task.FromResult(ImageImportChoice.Link),
        };
        Window.Width = 1440;
        Window.Height = 900;
        Window.Show();
    }

    public string Docs { get; }

    public string AppData => Path.Combine(_root, "appdata");

    public ScriptedDialogs Dialogs { get; }

    public NullLauncher Launcher { get; }

    public ShellViewModel Shell { get; }

    public MainWindow Window { get; }

    public async Task<string> WriteAsync(string name, string text)
    {
        var path = Path.Combine(Docs, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    public void Dispose()
    {
        Dialogs.SaveChoice = SaveChoice.Discard;
        Window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var closeDeadline = System.Diagnostics.Stopwatch.StartNew();
        while (Window.IsVisible && closeDeadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            Thread.Sleep(5);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        Shell.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }
}
