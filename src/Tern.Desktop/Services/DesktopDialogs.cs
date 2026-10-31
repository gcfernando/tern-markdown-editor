using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Tern.App.Documents;
using Tern.App.Shell;
using Tern.Domain.Merging;

namespace Tern.Desktop.Services;

/// <summary>The real dialogs behind <see cref="IShellDialogs"/>: modal prompts over the main window and the platform file pickers.</summary>
public sealed class DesktopDialogs : IShellDialogs
{
    private static readonly FilePickerFileType MarkdownFiles = new("Markdown and text")
    {
        Patterns = ["*.md", "*.markdown", "*.mdown", "*.txt"],
    };

    public Window? Owner { get; set; }

    public async Task<bool> ConfirmDeleteAsync(string path, int unsavedTabCount) =>
        await Ask("Move file to Recycle Bin?", $"Move “{Path.GetFileName(path)}” to the Windows Recycle Bin and remove its tabs, history and references?"
            + (unsavedTabCount > 0 ? $"\n\n{unsavedTabCount} open tab(s) have unsaved edits. Those edits will be discarded." : ""),
            ["Move to Recycle Bin", "Cancel"], 1, 1) == 0;

    public async Task<DuplicateOpenChoice> AskDuplicateOpenAsync(string documentName) =>
        await Ask("Already open", $"“{documentName}” is already open. Replace that tab or open another tab?",
            ["Replace existing tab", "Open in new tab", "Cancel"], 2, 2) switch
        { 0 => DuplicateOpenChoice.Replace, 1 => DuplicateOpenChoice.NewTab, _ => DuplicateOpenChoice.Cancel };

    public async Task<SaveChoice> AskSaveChangesAsync(string documentName)
    {
        var answer = await Ask("Unsaved changes", $"Save changes to “{documentName}” before closing?", ["Save", "Don't save", "Cancel"], 0, 2);
        return answer switch
        {
            0 => SaveChoice.Save,
            1 => SaveChoice.Discard,
            _ => SaveChoice.Cancel,
        };
    }

    public async Task<ConflictChoice> AskConflictAsync(DocumentTab tab)
    {
        var answer = await Ask(
            "File changed on disk",
            $"“{tab.DisplayName}” was changed outside Tern and you also have unsaved edits.",
            ["Replace disk with mine", "Use disk version", "Merge…", "Save my copy", "Cancel"], 2, 4);
        return answer switch
        {
            0 => ConflictChoice.KeepMine,
            1 => ConflictChoice.TakeDisk,
            2 => ConflictChoice.Merge,
            3 => ConflictChoice.SaveCopy,
            _ => ConflictChoice.Cancel,
        };
    }

    public async Task<MissingFileChoice> AskFileMissingAsync(DocumentTab tab)
    {
        var answer = await Ask(
            "File no longer exists",
            $"“{tab.DisplayName}” was moved or deleted. Your text is still open and nothing is lost until you close the tab.",
            ["Keep editing", "Close tab"], 0, 0);
        return answer == 1 ? MissingFileChoice.Close : MissingFileChoice.KeepEditing;
    }

    public async Task<string?> ResolveMergeAsync(DocumentTab tab, MergeResult merge)
    {
        if (Owner is null)
        {
            return null;
        }

        return await new Views.MergeWindow(tab.DisplayName, merge).ShowDialog<string?>(Owner);
    }

    public async Task<IReadOnlyList<string>> PickFilesToOpenAsync()
    {
        if (Owner?.StorageProvider is not { CanOpen: true } storage)
        {
            return [];
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open files",
            AllowMultiple = true,
            FileTypeFilter = [MarkdownFiles, FilePickerFileTypes.All],
        });
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    public Task<string?> PickFolderAsync() => PickFolderAsync("Open Folder — browse a physical directory");

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Owner?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<string?> PickSavePathAsync(string suggestedName, string? initialDirectory)
    {
        if (Owner?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        IStorageFolder? start = initialDirectory is null ? null : await storage.TryGetFolderFromPathAsync(initialDirectory);
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save as",
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = start,
            DefaultExtension = "md",
            FileTypeChoices = [MarkdownFiles, FilePickerFileTypes.All],
            ShowOverwritePrompt = false,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<bool> ConfirmOverwriteAsync(string path) =>
        await Ask("Replace file?", $"“{Path.GetFileName(path)}” already exists. Replace it?", ["Replace", "Cancel"], 1, 1) == 0;

    public async Task<bool> ConfirmReadOnlyCopyAsync(string documentName) =>
        await Ask("Save displayed text as a copy?",
            $"“{documentName}” contains bytes that could not be decoded exactly. The copy will contain the displayed text in UTF-8, including any replacement characters. The original file will be kept. Reopen with the correct encoding first if you need to recover the original characters.",
            ["Save UTF-8 copy", "Cancel"], 1, 1) == 0;

    public async Task<bool> ConfirmOpenExternalAsync(Uri uri) =>
        await Ask("Open link outside Tern?", $"This opens your default application for:\n{uri}", ["Open", "Cancel"], 1, 1) == 0;

    public void ShowMessage(string title, string message) => _ = Ask(title, message, ["OK"], 0, 0);

    private Task<int> Ask(string title, string message, string[] buttons, int defaultIndex, int cancelIndex) =>
        Owner is null
            ? Task.FromResult(cancelIndex)
            : Views.PromptWindow.AskAsync(Owner, title, message, buttons, defaultIndex, cancelIndex);
}
