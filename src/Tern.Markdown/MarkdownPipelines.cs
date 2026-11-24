using Markdig;
using Markdig.Extensions.EmphasisExtras;

namespace Tern.Markdown;

/// <summary>How much syntax beyond CommonMark is recognised. The profile is part of the document's identity, so rendering never silently varies.</summary>
public enum MarkdownProfile
{
    /// <summary>CommonMark only, no extensions.</summary>
    CommonMark,

    /// <summary>CommonMark plus GitHub Flavored Markdown tables, task lists, strikethrough, autolinks.</summary>
    Gfm,

    /// <summary>GFM plus footnotes, definition lists, math, alerts and YAML front matter.</summary>
    Extended,
}

public static class MarkdownPipelines
{
    private static readonly object CancellationContextKey = new();
    private static readonly Lazy<MarkdownPipeline> CommonMark = new(() => Build(MarkdownProfile.CommonMark));
    private static readonly Lazy<MarkdownPipeline> Gfm = new(() => Build(MarkdownProfile.Gfm));
    private static readonly Lazy<MarkdownPipeline> Extended = new(() => Build(MarkdownProfile.Extended));

    public static MarkdownPipeline For(MarkdownProfile profile) => profile switch
    {
        MarkdownProfile.CommonMark => CommonMark.Value,
        MarkdownProfile.Gfm => Gfm.Value,
        MarkdownProfile.Extended => Extended.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    public static Markdig.Syntax.MarkdownDocument Parse(string text, MarkdownProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!cancellationToken.CanBeCanceled) return Markdig.Markdown.Parse(text, For(profile));
        var context = new MarkdownParserContext();
        context.Properties[CancellationContextKey] = cancellationToken;
        return Markdig.Markdown.Parse(text, For(profile), context);
    }

    private static void ObserveCancellation(MarkdownParserContext? context)
    {
        if (context?.Properties.TryGetValue(CancellationContextKey, out var value) == true && value is CancellationToken cancellationToken)
            cancellationToken.ThrowIfCancellationRequested();
    }

    private static MarkdownPipeline Build(MarkdownProfile profile)
    {
        // Precise source locations are what make editor <-> preview sync and structured edits possible.
        var builder = new MarkdownPipelineBuilder().UsePreciseSourceLocation();
        if (profile >= MarkdownProfile.Gfm)
        {
            builder.UsePipeTables()
                .UseTaskLists()
                .UseAutoLinks()
                .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
                .UseYamlFrontMatter();
        }

        if (profile >= MarkdownProfile.Extended)
        {
            builder.UseFootnotes()
                .UseDefinitionLists()
                .UseMathematics()
                .UseAlertBlocks();
        }

        var pipeline = builder.Build();
        foreach (var parser in builder.BlockParsers)
            parser.Closed += static (processor, _) => ObserveCancellation(processor.Context);
        return pipeline;
    }
}
