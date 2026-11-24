using Tern.Rendering.Resources;

namespace Tern.App.Shell;

public enum DuplicateOpenChoice { Replace, NewTab, Cancel }
public enum DuplicateOpenBehavior { Activate, Ask, NewTab }
public enum TabCloseScope { This, All, Right, Others }

/// <summary>Why a tab became current. Only user activation may surface disk-change decisions (D-02).</summary>
public enum TabActivationReason { Programmatic, User }

public interface ILocalFileDeletion
{
    Task MoveToRecycleBinAsync(string path);
}

public enum SaveChoice
{
    Save,
    Discard,
    Cancel,
}

public enum ConflictChoice
{
    KeepMine,
    TakeDisk,
    Merge,
    SaveCopy,
    Cancel,
}

public enum MissingFileChoice
{
    KeepEditing,
    Close,
}

public enum MetadataFailureChoice
{
    Retry,
    Continue,
    Cancel,
}

/// <summary>
/// Everything the shell needs from the user interface. The views implement it with real dialogs; keeping it an interface
/// lets the whole open / save / conflict / close workflow be exercised against real files without a window.
/// </summary>
public interface IShellDialogs
{
    /// <summary>Asked after all document decisions succeeded but session/workspace layout could not be written. Never blocks exit by itself.</summary>
    Task<MetadataFailureChoice> AskMetadataSaveFailureAsync(string details, bool exiting) => Task.FromResult(MetadataFailureChoice.Cancel);
    Task<DuplicateOpenChoice> AskDuplicateOpenAsync(string documentName) => Task.FromResult(DuplicateOpenChoice.Cancel);
    Task<bool> ConfirmDeleteAsync(string path, int unsavedTabCount) => Task.FromResult(false);

    /// <summary>Confirms recycling a folder and everything in it. The destructive choice must never be the default button.</summary>
    Task<bool> ConfirmDeleteFolderAsync(string path, int fileCount, int openDocumentCount, int unsavedTabCount) => Task.FromResult(false);
    Task<SaveChoice> AskSaveChangesAsync(string documentName);

    Task<ConflictChoice> AskConflictAsync(Documents.DocumentTab tab);

    Task<MissingFileChoice> AskFileMissingAsync(Documents.DocumentTab tab);

    /// <summary>Asks before a save recreates a folder that no longer exists (S-02). <paramref name="movedTo"/> is where the file went, when known.</summary>
    Task<RecreateFolderChoice> AskRecreateFolderAsync(string folder, string? movedTo) => Task.FromResult(RecreateFolderChoice.Cancel);

    /// <summary>Shows the three-way merge editor and returns the text the user accepted, or null if they cancelled.</summary>
    Task<string?> ResolveMergeAsync(Documents.DocumentTab tab, Domain.Merging.MergeResult merge);

    /// <summary>Like the two-argument overload; <paramref name="savesOnAccept"/> tells the dialog whether accepting also writes the file.</summary>
    Task<string?> ResolveMergeAsync(Documents.DocumentTab tab, Domain.Merging.MergeResult merge, bool savesOnAccept) => ResolveMergeAsync(tab, merge);

    /// <summary>Asks before a reload throws away unsaved edits. The destructive choice must never be the default button.</summary>
    Task<bool> ConfirmReloadDiscardingEditsAsync(Documents.DocumentTab tab) => Task.FromResult(false);

    Task<IReadOnlyList<string>> PickFilesToOpenAsync();

    Task<string?> PickFolderAsync();
    Task<string?> PickFolderAsync(string title) => PickFolderAsync();

    Task<string?> PickSavePathAsync(string suggestedName, string? initialDirectory);

    Task<bool> ConfirmOverwriteAsync(string path);

    Task<bool> ConfirmReadOnlyCopyAsync(string documentName) => Task.FromResult(false);
    Task<Documents.OutsideAttachmentChoice> AskOutsideAttachmentsAsync(IReadOnlyList<string> references) =>
        Task.FromResult(Documents.OutsideAttachmentChoice.Cancel);

    /// <summary>Asks before a descriptor file is written into the user's own folder.</summary>
    Task<bool> ConfirmPromoteFolderAsync(string folderPath) => Task.FromResult(false);

    Task<bool> ConfirmOpenExternalAsync(Uri uri);

    void ShowMessage(string title, string message);
}

/// <summary>Opens a URL or reveals a file with the operating system; separated so link handling is testable.</summary>
public interface IExternalLauncher
{
    void OpenUri(Uri uri);

    void RevealInFileManager(string path);
}

/// <summary>What happened when the user clicked a link in the preview.</summary>
public enum LinkOutcome
{
    OpenedDocument,
    ScrolledToAnchor,
    OpenedExternal,
    DeclinedExternal,
    FileMissing,
    Blocked,
    OpenedWithSystem,
}

public sealed record LinkActivation(LinkOutcome Outcome, string? Path = null, string? Fragment = null, string? Message = null);

internal static class ResourceDecisionDescriber
{
    public static string Describe(Blocked blocked) => $"Link blocked: {blocked.Detail}";
}
