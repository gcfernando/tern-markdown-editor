using Tern.App.Shell;

namespace Tern.App.Documents;

/// <summary>Failure takes precedence over a previously saved identity; checkpoints never imply disk persistence.</summary>
public sealed record DocumentProtection(string Label, ActivitySeverity Severity, string Details)
{
    public static DocumentProtection For(DocumentTab tab, DocumentState state, bool privateDocument = false)
    {
        var checkpoint = privateDocument ? "Private workspace: History, recovery checkpoints and discarded-draft safety copies are disabled. Original files are not encrypted."
            : tab.CheckpointRevision == tab.Session.Revision ? "Recovery checkpoint protects the current revision. It is not a disk save."
            : "A recovery checkpoint for the current revision has not been confirmed.";
        var identity = tab.FilePath ?? "No saved file";
        if (tab.SourceUrl is { } url) identity += "\nFrom URL: " + url;
        var details = identity + "\n" + checkpoint;
        if (tab.LastSaveError is { } saveError) return new("Save failed", ActivitySeverity.Error, details + "\n" + saveError);
        if (!privateDocument && tab.RecoveryError is { } recoveryError) return new("Recovery failed", ActivitySeverity.Error, details + "\n" + recoveryError);
        if (state == DocumentState.Inaccessible)
            return new("Unavailable — will retry", ActivitySeverity.Warning, details + "\nYour open text is kept. Tern checks again when you return to this document; Save works once the location is back.");
        if (state == DocumentState.Missing)
            return new("File missing", ActivitySeverity.Warning, details + "\nYour open text is kept. Save a copy to another location.");
        if (state == DocumentState.Saving) return new("Saving…", ActivitySeverity.Neutral, details);
        if (state == DocumentState.ChangedOnDisk)
            return new("Changed on disk", ActivitySeverity.Warning, details + (tab.IsDirty ? "\nYour unsaved edits are kept. Compare, reload the disk version, or keep yours." : "\nYou have no unsaved edits. Reload to see the new version."));
        if (tab.IsReadOnly) return new("Read-only", ActivitySeverity.Neutral, details + "\n" + tab.ReadOnlyReason);
        if (privateDocument) return new(tab.IsDirty ? "Unsaved · Private" : tab.IsUntitled ? "Not saved · Private" : "Saved · Private", ActivitySeverity.Neutral, details);
        if (tab.IsDirty) return new(tab.CheckpointRevision == tab.Session.Revision ? "Unsaved · recovery protected" : "Unsaved · recovery pending", ActivitySeverity.Neutral, details);
        return new(tab.IsUntitled ? "Not saved to file" : "Saved to file", tab.IsUntitled ? ActivitySeverity.Neutral : ActivitySeverity.Success, details);
    }
}
