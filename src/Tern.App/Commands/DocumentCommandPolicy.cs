using Tern.Domain.Documents;
using Tern.Domain.Text;

namespace Tern.App.Commands;

public sealed record DocumentCommandContext(DocumentSession? Document, bool HasWorkspace = false,
    int OpenDocumentCount = 0, bool CanUndo = false, bool CanRedo = false,
    bool IsBusy = false, bool HasDirtyDocuments = false, bool HasFolder = false);

/// <summary>Shared eligibility rules. Hosts must check these again when executing a command.</summary>
public static class DocumentCommandPolicy
{
    // Retained for existing hosts; opening a file never creates a pending conversion.
    public static bool RequiresCrlfSave(DocumentSession document) => false;

    public static bool IsOutputCommand(string id) => id is CommandIds.Print or CommandIds.ExportHtml or CommandIds.ExportPdf;

    public static string? DisabledReason(string id, DocumentCommandContext context) =>
        IsOutputCommand(id) && !CanExecute(id, context)
            ? context.IsBusy ? "Please wait for the current operation to finish." : "Open a file or write something first."
            : null;

    public static bool CanExecute(string id, DocumentCommandContext context)
    {
        var document = context.Document;
        var editable = document is { IsReadOnly: false } && !context.IsBusy;
        return id switch
        {
            CommandIds.OpenUrl => !context.IsBusy,
            CommandIds.Save => editable && (document!.IsDirty || !document.HasFile || document is { HasFile: true, Baseline: null }),
            CommandIds.SaveAs => document is not null && !context.IsBusy,
            CommandIds.SaveAll => !context.IsBusy && context.HasDirtyDocuments,
            CommandIds.Undo => editable && context.CanUndo,
            CommandIds.Redo => editable && context.CanRedo,
            CommandIds.Rename => document is not null && !context.IsBusy,
            CommandIds.Replace or CommandIds.ChangeEncoding or CommandIds.ChangeLineEnding
                or CommandIds.InsertImage or CommandIds.PasteImage => editable,
            CommandIds.NewWorkspaceFile or CommandIds.NewFolder
                or CommandIds.CloseWorkspace or CommandIds.CloseAllWorkspaces => (context.HasWorkspace || context.HasFolder) && !context.IsBusy,
            CommandIds.FindInWorkspace => context.HasWorkspace && !context.IsBusy,
            CommandIds.CloseDocuments => context.OpenDocumentCount > 0 && !context.IsBusy,
            CommandIds.NextTab or CommandIds.PreviousTab => context.OpenDocumentCount > 1,
            CommandIds.ReopenWithEncoding => document is { HasFile: true, IsDirty: false } && !context.IsBusy,
            CommandIds.LibraryBacklinks => context.HasWorkspace && document is { HasFile: true },
            CommandIds.LibrarySearch or CommandIds.LibraryBrokenLinks or CommandIds.LibraryTasks => context.HasWorkspace,
            CommandIds.ReloadFromDisk or CommandIds.RevealInExplorer
                or CommandIds.ShowHistory => document is { HasFile: true } && !context.IsBusy,
            CommandIds.Print or CommandIds.ExportHtml or CommandIds.ExportPdf => document is not null && !context.IsBusy
                && (document.HasFile || !string.IsNullOrWhiteSpace(document.Buffer.GetText())),
            CommandIds.Find or CommandIds.GoToLine or CommandIds.GoToHeading or CommandIds.CloseTab
                or CommandIds.ViewSource or CommandIds.ViewSplit or CommandIds.ViewReading
                or CommandIds.ToggleReference => document is not null && !context.IsBusy,
            _ when id.StartsWith("format.", StringComparison.Ordinal) || id.StartsWith("insert.", StringComparison.Ordinal)
                || id is CommandIds.ToggleTaskAtCaret or CommandIds.AskAI => editable,
            _ => true,
        };
    }
}
