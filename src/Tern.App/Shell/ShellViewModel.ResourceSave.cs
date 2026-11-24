using Tern.App.Documents;
using Tern.Filesystem;

namespace Tern.App.Shell;

public sealed partial class ShellViewModel
{
    private sealed record SaveCancelled : SaveOutcome;

    private async Task<SaveOutcome> SaveWithResourceChoiceAsync(DocumentTab tab, Func<OutsideAttachmentChoice, IReadOnlySet<string>?, Task<SaveOutcome>> save,
        ActivityScope? activity = null)
    {
        var result = await save(OutsideAttachmentChoice.Cancel, null).ConfigureAwait(true);
        if (result is not SaveFailed { Exception: OutsideAttachmentsException outside }) return result;
        var revision = tab.Session.Revision;
        var path = tab.FilePath;
        activity?.WaitForUser("Review attachment choices before saving");
        var choice = await _dialogs.AskOutsideAttachmentsAsync(outside.References).ConfigureAwait(true);
        if (choice == OutsideAttachmentChoice.Cancel)
        {
            SetStatus("Save cancelled. The original document and attachments were kept.", StatusSeverity.Info);
            return new SaveCancelled();
        }
        if (_disposed || !tab.Session.IsCurrent(revision) || tab.FilePath != path)
            return new SaveFailed("The document changed while reviewing attachments. Review Save As again; your newer edits were kept.");
        return await save(choice, outside.References.ToHashSet(Tern.Workspaces.WorkspacePaths.Comparer)).ConfigureAwait(true);
    }
}
