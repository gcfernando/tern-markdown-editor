using Tern.Search;
using Tern.Workspaces;

namespace Tern.App.Editing;

/// <summary>The matches of one in-document search.</summary>
public sealed record InFileFindResult(IReadOnlyList<FindMatch> Matches, string? Error, bool Truncated)
{
    public static InFileFindResult Empty { get; } = new([], null, false);

    /// <summary>The index of the first match at or after <paramref name="offset"/>, wrapping to the start; -1 when there are none.</summary>
    public int IndexAtOrAfter(int offset)
    {
        for (var i = 0; i < Matches.Count; i++)
        {
            if (Matches[i].Start >= offset)
            {
                return i;
            }
        }

        return Matches.Count == 0 ? -1 : 0;
    }

    /// <summary>The index of the last match that ends before <paramref name="offset"/>, wrapping to the end; -1 when there are none.</summary>
    public int IndexBefore(int offset)
    {
        for (var i = Matches.Count - 1; i >= 0; i--)
        {
            if (Matches[i].Start < offset)
            {
                return i;
            }
        }

        return Matches.Count == 0 ? -1 : Matches.Count - 1;
    }
}

/// <summary>Find and replace inside one buffer, using the same matcher (and regex time limit) as workspace search.</summary>
public static class InFileFinder
{
    public static async Task<InFileFindResult> FindAsync(string text, FindOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(options.Pattern))
        {
            return InFileFindResult.Empty;
        }

        const string BufferPath = "buffer";
        var finder = new FindReplace(new SingleTextProvider(BufferPath, text));
        var preview = await finder.PreviewAsync([BufferPath], options, cancellationToken).ConfigureAwait(false);
        if (preview.HasError)
        {
            return new InFileFindResult([], preview.PatternError, false);
        }

        if (preview.Files.Count == 0)
        {
            return InFileFindResult.Empty;
        }

        var file = preview.Files[0];

        var error = file.TimedOut ? "The pattern took too long to evaluate." : null;
        return new InFileFindResult(file.Matches, error, file.Truncated);
    }

    private sealed class SingleTextProvider(string path, string text) : IDocumentTextProvider
    {
        public ValueTask<string?> ReadTextAsync(string requested, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(string.Equals(requested, path, StringComparison.Ordinal) ? text : null);
    }
}
