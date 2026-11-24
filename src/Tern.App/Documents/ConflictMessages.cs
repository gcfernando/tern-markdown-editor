namespace Tern.App.Documents;

/// <summary>Wording for disk-change prompts, kept free of UI types so it can be tested directly (B-29).</summary>
public static class ConflictMessages
{
    public const string NotLosslessNote = "The changed file uses another encoding, so it cannot be merged without damaging its characters.";

    public static string Describe(string documentName, bool isDirty, bool canMerge)
    {
        var message = isDirty
            ? $"“{documentName}” was changed outside Tern and you also have unsaved edits."
            : $"“{documentName}” was changed outside Tern. You have no unsaved edits in Tern.";
        return canMerge ? message : message + "\n\n" + NotLosslessNote;
    }
}
