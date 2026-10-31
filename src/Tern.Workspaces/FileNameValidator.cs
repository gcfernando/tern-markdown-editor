namespace Tern.Workspaces;

public enum NameProblem
{
    None,
    Empty,
    TooLong,
    IllegalCharacter,
    ReservedName,
    TrailingDotOrSpace,
    LeadingSpace,
    DotName,
}

public readonly record struct NameValidation(NameProblem Problem, string? Message)
{
    public bool IsValid => Problem == NameProblem.None;
}

/// <summary>
/// Validates a single file or folder name so that it is legal on every platform Tern runs on, not only the current one.
/// Notes made on Linux must still open on a Windows machine that syncs the same folder.
/// </summary>
public static class FileNameValidator
{
    public const int MaxLength = 255;

    private static readonly char[] IllegalCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    public static NameValidation Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fail(NameProblem.Empty, "The name cannot be empty.");
        }

        if (name.Length > MaxLength)
        {
            return Fail(NameProblem.TooLong, $"The name is longer than {MaxLength} characters.");
        }

        if (name is "." or "..")
        {
            return Fail(NameProblem.DotName, "\".\" and \"..\" are not valid names.");
        }

        foreach (var c in name)
        {
            if (c < 32 || Array.IndexOf(IllegalCharacters, c) >= 0)
            {
                return Fail(NameProblem.IllegalCharacter, c < 32
                    ? "The name contains a control character."
                    : $"The name cannot contain \"{c}\".");
            }
        }

        if (name[^1] is '.' or ' ')
        {
            return Fail(NameProblem.TrailingDotOrSpace, "The name cannot end with a dot or a space.");
        }

        if (name[0] == ' ')
        {
            return Fail(NameProblem.LeadingSpace, "The name cannot start with a space.");
        }

        // Windows reserves these names even when an extension follows ("CON.md" is not creatable).
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        if (ReservedNames.Contains(stem))
        {
            return Fail(NameProblem.ReservedName, $"\"{stem}\" is a reserved name on Windows.");
        }

        return new NameValidation(NameProblem.None, null);
    }

    private static NameValidation Fail(NameProblem problem, string message) => new(problem, message);
}
