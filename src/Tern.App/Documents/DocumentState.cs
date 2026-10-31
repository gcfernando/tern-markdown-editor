using Tern.Domain.Documents;

namespace Tern.App.Documents;

public enum DocumentState
{
    NoDocument, NewEmpty, NewModified, SavedClean, SavedModified,
    Loading, Saving, Missing, Inaccessible, ReadOnly,
}

public enum DocumentOperation { None, Loading, Saving }

/// <summary>Pure state classification; callers provide observed I/O status rather than probing the filesystem.</summary>
public static class DocumentStateResolver
{
    public static DocumentState Resolve(DocumentSession? document, DocumentOperation operation = DocumentOperation.None,
        bool missing = false, bool inaccessible = false)
    {
        if (operation == DocumentOperation.Loading) return DocumentState.Loading;
        if (document is null) return DocumentState.NoDocument;
        if (operation == DocumentOperation.Saving) return DocumentState.Saving;
        if (missing) return DocumentState.Missing;
        if (inaccessible) return DocumentState.Inaccessible;
        if (document.IsReadOnly) return DocumentState.ReadOnly;
        if (!document.HasFile) return document.Buffer.GetText().Length == 0 ? DocumentState.NewEmpty : DocumentState.NewModified;
        return document.IsDirty ? DocumentState.SavedModified : DocumentState.SavedClean;
    }
}
