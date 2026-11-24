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
        await Ask("Move file to Recycle Bin?", $"Move “{Path.GetFileName(path)}” to the Windows Recycle Bin and remove its tabs and references?\n\nFile: {path}\n\nHistory is kept for 30 days and restored if the file comes back."
            + (unsavedTabCount > 0 ? $"\n\n{unsavedTabCount} open tab(s) have unsaved edits. Those edits will be discarded." : ""),
            ["Move to Recycle Bin", "Cancel"], 1, 1, destructiveIndex: 0) == 0;

    public async Task<bool> ConfirmDeleteFolderAsync(string path, int fileCount, int openDocumentCount, int unsavedTabCount) =>
        await Ask("Move folder to Recycle Bin?",
            $"Move the folder “{Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}” and everything in it ({fileCount} file(s)) to the Windows Recycle Bin?\n\nIts open tabs, recent entries and link-graph entries are removed from Tern. History is kept for 30 days and restored if the files come back."
            + (openDocumentCount > 0 ? $"\n\n{openDocumentCount} open document(s) inside the folder will be closed." : "")
            + (unsavedTabCount > 0 ? $"\n\n{unsavedTabCount} open tab(s) have unsaved edits. Those edits will be discarded." : ""),
            ["Move folder to Recycle Bin", "Cancel"], 1, 1, destructiveIndex: 0) == 0;

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

    public async Task<MetadataFailureChoice> AskMetadataSaveFailureAsync(string details, bool exiting)
    {
        var leave = exiting ? "Exit anyway" : "Close anyway";
        var answer = await Ask(exiting ? "Could not save layout" : "Could not save workspace layout",
            "Tern couldn't save the session/workspace layout. Your documents are saved.\n\n" + details,
            ["Retry", leave, "Cancel"], 0, 2, destructiveIndex: 1);
        return answer switch { 0 => MetadataFailureChoice.Retry, 1 => MetadataFailureChoice.Continue, _ => MetadataFailureChoice.Cancel };
    }

    public async Task<ConflictChoice> AskConflictAsync(DocumentTab tab)
    {
        var canMerge = tab.Conflict?.DiskNotLossless != true;
        string[] buttons = canMerge
            ? ["Replace disk with mine", "Use disk version", "Merge…", "Save my copy", "Cancel"]
            : ["Replace disk with mine", "Use disk version", "Save my copy", "Cancel"];
        var message = Tern.App.Documents.ConflictMessages.Describe(tab.DisplayName, tab.IsDirty, canMerge);
        var answer = await Ask("File changed on disk", message, buttons, 2, buttons.Length - 1);
        if (!canMerge && answer >= 2) answer++;
        return answer switch
        {
            0 => ConflictChoice.KeepMine,
            1 => ConflictChoice.TakeDisk,
            2 => ConflictChoice.Merge,
            3 => ConflictChoice.SaveCopy,
            _ => ConflictChoice.Cancel,
        };
    }

    public async Task<RecreateFolderChoice> AskRecreateFolderAsync(string folder, string? movedTo)
    {
        var message = $"The folder “{folder}” no longer exists, so saving here would create it again." + (movedTo is null ? string.Empty
            : $"\n\nThis document was moved to “{movedTo}”. Close this prompt and choose Follow in the bar above the document to keep working there.")
            + "\n\nSave As lets you choose another location.";
        var answer = await Ask("Recreate folder?", message, ["Recreate folder", "Save As…", "Cancel"], 1, 2);
        return answer switch { 0 => RecreateFolderChoice.Recreate, 1 => RecreateFolderChoice.SaveAs, _ => RecreateFolderChoice.Cancel };
    }

    public async Task<MissingFileChoice> AskFileMissingAsync(DocumentTab tab)
    {
        var answer = await Ask(
            "File no longer exists",
            $"“{tab.DisplayName}” was moved or deleted. Your text is still open and nothing is lost until you close the tab.",
            ["Keep editing", "Close tab"], 0, 0);
        return answer == 1 ? MissingFileChoice.Close : MissingFileChoice.KeepEditing;
    }

    public async Task<string?> ResolveMergeAsync(DocumentTab tab, MergeResult merge) => await ResolveMergeAsync(tab, merge, savesOnAccept: false);

    public async Task<string?> ResolveMergeAsync(DocumentTab tab, MergeResult merge, bool savesOnAccept)
    {
        if (Owner is null)
        {
            return null;
        }

        return await new Views.MergeWindow(tab.DisplayName, merge, savesOnAccept).ShowDialog<string?>(Owner);
    }

    public async Task<bool> ConfirmReloadDiscardingEditsAsync(DocumentTab tab) =>
        await Ask("Reload from disk?", $"Reloading “{tab.DisplayName}” discards your unsaved edits and shows the version on disk.",
            ["Discard my edits and reload", "Cancel"], 1, 1, destructiveIndex: 0) == 0;

    public async Task<OutsideAttachmentChoice> AskOutsideAttachmentsAsync(IReadOnlyList<string> references)
    {
        var answer = await Ask("Outside attachments",
            $"{references.Count} attachment(s) are outside the original note's folder:\n\n" + string.Join("\n", references)
            + "\n\nSave without copying leaves these links unchanged; they may break in the new location. Absolute paths keep pointing to the originals but are not portable and can reveal local folder names.",
            [$"Save without copying {references.Count} outside attachments", "Rewrite links to absolute", "Cancel"], 2, 2);
        return answer switch { 0 => OutsideAttachmentChoice.SaveWithoutCopying, 1 => OutsideAttachmentChoice.RewriteAbsolute, _ => OutsideAttachmentChoice.Cancel };
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

    public async Task<bool> ConfirmPromoteFolderAsync(string folderPath) =>
        await Ask("Make this folder a workspace?",
            $"Tern will add a small file named {Tern.Workspaces.WorkspaceDefinition.FileName} to:\n{folderPath}\n\nIts documents are then indexed for content search, backlinks, broken-link checks and tasks. No files are moved, renamed or changed. Delete that file to undo.",
            ["Make workspace", "Cancel"], 0, 1) == 0;

    public async Task<bool> ConfirmOpenExternalAsync(Uri uri) =>
        await Ask("Open link outside Tern?", $"This opens your default application for:\n{uri}", ["Open", "Cancel"], 1, 1) == 0;

    public void ShowMessage(string title, string message) => _ = Ask(title, message, ["OK"], 0, 0);

    private Task<int> Ask(string title, string message, string[] buttons, int defaultIndex, int cancelIndex, int destructiveIndex = -1) =>
        Owner is null
            ? Task.FromResult(cancelIndex)
            : Views.PromptWindow.AskAsync(Owner, title, message, buttons, defaultIndex, cancelIndex, destructiveIndex);
}
