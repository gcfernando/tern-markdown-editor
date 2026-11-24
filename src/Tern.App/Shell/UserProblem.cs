using System.ComponentModel;
using System.Text.Json;

namespace Tern.App.Shell;

/// <summary>Turns a failure into what happened, the likely reason and what the user can do next.</summary>
public sealed record UserProblem(string Title, string Why, string Next)
{
    public string Text => $"{Title}. {Why} {Next}";

    /// <param name="includeMessage">False where the raw message must stay out of the visible text and belongs in the report.</param>
    public static UserProblem Explain(Exception exception, string title, bool includeMessage = true)
    {
        var error = exception is AggregateException aggregate ? aggregate.GetBaseException() : exception;
        var code = error.HResult & 0xFFFF;
        return error switch
        {
            UnauthorizedAccessException => new(title, "Windows denied access to this file or folder.",
                "Check that it is not read-only and that your account has permission, then try again."),
            FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException => new(title,
                "The file, folder or drive is no longer at that location.", "Check that it was not moved or renamed and that the drive is connected, then try again."),
            PathTooLongException => new(title, "The file path is longer than Windows allows.", "Move the folder closer to the drive root or shorten its name."),
            IOException when code is 32 or 33 => new(title, "Another program is using this file.", "Close that program or wait for sync to finish, then try again."),
            IOException when code is 39 or 112 => new(title, "The drive is full.", "Free some disk space, then try again."),
            IOException when code is 53 or 64 or 67 => new(title, "The network location is not reachable.", "Check the network connection, then try again."),
            TimeoutException or TaskCanceledException => new(title, "The operation took too long.", "Check the connection or file location, then try again."),
            JsonException or InvalidDataException => new(title, "A saved Tern file is damaged or has an unexpected format.", "Restore that file from a backup, or use Copy report if this continues."),
            System.Runtime.InteropServices.ExternalException when !includeMessage => new(title, "Windows could not complete the operation.", "Try again. If it continues, use Details or Copy report."),
            _ when includeMessage => new(title, Sentence(error.Message), "If this continues, use Copy report from the Tern error banner."),
            _ => new(title, "Tern hit an unexpected problem.", "Try again. If it continues, choose Details or Copy report."),
        };
    }

    /// <summary>A reason safe to show people. Tern's own messages stay. System wording becomes a plain reason and next step.</summary>
    public static string Plain(Exception exception)
    {
        var error = exception is AggregateException aggregate ? aggregate.GetBaseException() : exception;
        if (error is ArgumentException argument && argument.ParamName is { Length: > 0 } name)
        {
            var suffix = $" (Parameter '{name}')";
            var message = argument.Message.EndsWith(suffix, StringComparison.Ordinal) ? argument.Message[..^suffix.Length] : argument.Message;
            return Sentence(message);
        }
        if (error is FileNotFoundException { FileName: { Length: > 0 } missing })
            return $"{Path.GetFileName(missing)} is no longer at that location. Check that it was not moved or renamed, then try again.";
        const int plainIOError = unchecked((int)0x80131620);
        var ownMessage = error is ArgumentException or InvalidOperationException or FormatException or InvalidDataException
            || (error.GetType() == typeof(IOException) && error.HResult == plainIOError);
        if (ownMessage && !LooksTechnical(error.Message)) return Sentence(error.Message);
        var problem = Explain(error, "", includeMessage: false);
        return problem.Why + " " + problem.Next;
    }

    private static bool LooksTechnical(string message) => new[] { "Sequence contains", "Object reference", "Nullable object", "Collection was modified", "Parameter '", "HRESULT", "0x8" }
        .Any(marker => message.Contains(marker, StringComparison.Ordinal));

    private static string Sentence(string message)
    {
        var text = message.Trim();
        return text.Length == 0 ? "No further reason was reported." : text.EndsWith('.') ? text : text + ".";
    }
}
