using Tern.Domain.Documents;
using Tern.Domain.Text;

namespace Tern.App.Commands;

public sealed record DocumentCommandContext(DocumentSession? Document, bool HasWorkspace = false,
    int OpenDocumentCount = 0, bool CanUndo = false, bool CanRedo = false,
    bool IsBusy = false, bool HasDirtyDocuments = false, bool HasFolder = false, bool HasBrowsedFolder = false, bool HasHistorySource = false);

/// <summary>Shared eligibility rules. Hosts must check these again when executing a command.</summary>
public static class DocumentCommandPolicy
{
    // Retained for existing hosts; opening a file never creates a pending conversion.
    public static bool RequiresCrlfSave(DocumentSession document) => false;

    public static bool IsOutputCommand(string id) => id is CommandIds.Print or CommandIds.ExportAs or CommandIds.ExportHtml or CommandIds.ExportPdf;

    public static string? DisabledReason(string id, DocumentCommandContext context)
    {
        if (CanExecute(id, context)) return null;
        if (context.IsBusy && CanExecute(id, context with { IsBusy = false }))
            return "Please wait for the current operation to finish.";
        if (id is CommandIds.FindInWorkspace or CommandIds.ReplaceInWorkspace or CommandIds.LibrarySearch or CommandIds.LibraryBrokenLinks
            or CommandIds.LibraryTasks or CommandIds.LibraryTags or CommandIds.LibraryMentions or CommandIds.LibraryGraph
            or CommandIds.LibraryBacklinks or CommandIds.OpenTodaysNote or CommandIds.WorkspacePrivacy
            or CommandIds.ClearWorkspaceHistory && !context.HasWorkspace)
            return "This action needs an open workspace. " + (context.HasBrowsedFolder
                ? "Make this folder a workspace to enable content search and workspace tools."
                : "Open a workspace first.");
        if (id is CommandIds.NewWorkspaceFile or CommandIds.NewFolder or CommandIds.CloseWorkspace
            or CommandIds.CloseAllWorkspaces or CommandIds.ReviewSaveFragments)
            return "Open a folder or workspace first.";
        if (id == CommandIds.PromoteFolder)
            return "Open a browsed folder first. Existing workspaces do not need to be made a workspace again.";
        if (id == CommandIds.SaveAll) return "There are no unsaved documents to save.";
        if (id is CommandIds.NextTab or CommandIds.PreviousTab) return "Open at least two documents to switch tabs.";
        if (id == CommandIds.CloseDocuments) return "There are no open documents to close.";
        if (IsOutputCommand(id)) return "Open a file or write something first.";
        var document = context.Document;
        if (document is null) return "Open a document first.";
        if (id is CommandIds.ShowHistory or CommandIds.ReloadFromDisk or CommandIds.RevealInExplorer
            or CommandIds.ReopenWithEncoding or CommandIds.LibraryBacklinks or CommandIds.ClearFileHistory or CommandIds.GitDiff
            && !document.HasFile)
            return "Save this document to a local file first.";
        if (id is CommandIds.MoveDocument or CommandIds.DuplicateDocument)
            return !document.HasFile ? "Save this document to a local file first."
                : "Open a workspace containing this document first.";
        if (id == CommandIds.ReopenWithEncoding && document.IsDirty)
            return "Save or explicitly discard unsaved changes before reopening with another encoding.";
        if (document.IsReadOnly) return "This document is read-only. Save a local copy to edit it.";
        return id switch
        {
            CommandIds.Save => "This document has no unsaved changes.",
            CommandIds.Undo => "There is nothing to undo in this document.",
            CommandIds.Redo => "There is nothing to redo in this document.",
            _ => "This action is unavailable for the current document.",
        };
    }

    public static bool CanExecute(string id, DocumentCommandContext context)
    {
        var document = context.Document;
        var editable = document is { IsReadOnly: false } && !context.IsBusy;
        return id switch
        {
            CommandIds.OpenUrl or CommandIds.NewFromTemplate => !context.IsBusy,
            CommandIds.OpenTodaysNote => context.HasWorkspace && !context.IsBusy,
            CommandIds.Save => editable && (document!.IsDirty || !document.HasFile || document is { HasFile: true, Baseline: null }),
            CommandIds.SaveAs => document is not null && !context.IsBusy,
            CommandIds.SaveAll => !context.IsBusy && context.HasDirtyDocuments,
            CommandIds.Undo => editable && context.CanUndo,
            CommandIds.Redo => editable && context.CanRedo,
            CommandIds.Rename => document is not null && !context.IsBusy,
            CommandIds.MoveDocument or CommandIds.DuplicateDocument => document is { HasFile: true } && context.HasWorkspace && !context.IsBusy,
            CommandIds.Cut or CommandIds.Paste or CommandIds.PastePlain or CommandIds.PasteTable or CommandIds.Replace or CommandIds.ChangeEncoding or CommandIds.ChangeLineEnding
                or CommandIds.InsertImage or CommandIds.PasteImage => editable,
            CommandIds.NewWorkspaceFile or CommandIds.NewFolder
                or CommandIds.CloseWorkspace or CommandIds.CloseAllWorkspaces => (context.HasWorkspace || context.HasFolder) && !context.IsBusy,
            CommandIds.PromoteFolder => context.HasBrowsedFolder && !context.IsBusy,
            CommandIds.FindInWorkspace or CommandIds.ReplaceInWorkspace => context.HasWorkspace && !context.IsBusy,
            CommandIds.WorkspacePrivacy or CommandIds.ClearWorkspaceHistory => context.HasWorkspace && !context.IsBusy,
            CommandIds.ClearFileHistory or CommandIds.GitDiff => document is { HasFile: true } && !context.IsBusy,
            CommandIds.RecentlyDiscarded => !context.IsBusy,
            CommandIds.CloseDocuments => context.OpenDocumentCount > 0 && !context.IsBusy,
            CommandIds.NextTab or CommandIds.PreviousTab => context.OpenDocumentCount > 1,
            CommandIds.ReopenWithEncoding => document is { HasFile: true, IsDirty: false } && !context.IsBusy,
            CommandIds.LibraryBacklinks or CommandIds.LibraryMentions or CommandIds.LibraryGraph => context.HasWorkspace && document is { HasFile: true },
            CommandIds.LibrarySearch or CommandIds.LibraryBrokenLinks or CommandIds.LibraryTasks or CommandIds.LibraryTags => context.HasWorkspace,
            CommandIds.ReloadFromDisk or CommandIds.RevealInExplorer => document is { HasFile: true } && !context.IsBusy,
            CommandIds.ReviewSaveFragments => context.HasWorkspace || context.HasFolder,
            CommandIds.ShowHistory => (document is { HasFile: true } || context.HasHistorySource) && !context.IsBusy,
            CommandIds.Print or CommandIds.ExportAs or CommandIds.ExportHtml or CommandIds.ExportPdf => document is not null && !context.IsBusy
                && (document.HasFile || !string.IsNullOrWhiteSpace(document.Buffer.GetText())),
            CommandIds.Copy or CommandIds.SelectAll or CommandIds.Find or CommandIds.GoToLine or CommandIds.GoToHeading or CommandIds.CloseTab
                or CommandIds.FoldSection
                or CommandIds.ViewSource or CommandIds.ViewSplit or CommandIds.ViewReading or CommandIds.ReadCurrentLine or CommandIds.ReadDocumentStatus
                or CommandIds.ToggleReference => document is not null && !context.IsBusy,
            _ when id.StartsWith("format.", StringComparison.Ordinal) || id.StartsWith("insert.", StringComparison.Ordinal)
                || id is CommandIds.ToggleTaskAtCaret or CommandIds.AskAI => editable,
            _ => true,
        };
    }
}
