using Tern.App.Editing;
using Tern.Search;

namespace Tern.App.Tests;

public sealed class DocumentSearchTests
{
    [Fact]
    public async Task Whole_words_do_not_split_combining_marks_or_identifiers()
    {
        var result = await InFileFinder.FindAsync("cafe cafe\u0301 cafe_name café CAFE", new FindOptions("cafe", WholeWord: true));
        Assert.Null(result.Error);
        Assert.Equal(new[] { 0, 26 }, result.Matches.Select(m => m.Start));
    }

    [Fact]
    public async Task Zero_width_regex_results_are_navigable_even_without_a_replacement()
    {
        var result = await InFileFinder.FindAsync("one\ntwo\nthree", new FindOptions("^", UseRegex: true));
        Assert.Null(result.Error);
        Assert.Equal(new[] { 0, 4, 8 }, result.Matches.Select(m => m.Start));
        Assert.All(result.Matches, m => Assert.Equal(0, m.Length));
    }

    [Fact]
    public async Task Superseded_search_observes_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InFileFinder.FindAsync(
            new string('a', 500_000), new FindOptions("a"), cancellation.Token));
    }

    [Fact]
    public async Task Large_search_reports_incomplete_results_instead_of_silent_loss()
    {
        var result = await InFileFinder.FindAsync(string.Join(' ', Enumerable.Repeat("needle", 6000)),
            new FindOptions("needle", MaxMatchesPerFile: 100));
        Assert.Null(result.Error);
        Assert.True(result.Truncated);
        Assert.Equal(100, result.Matches.Count);
        Assert.Equal(693, result.Matches[^1].Start);
        Assert.All(result.Matches, m =>
        {
            Assert.Empty(m.Before);
            Assert.Empty(m.After);
            Assert.Empty(m.ContextBefore);
            Assert.Empty(m.ContextAfter);
        });
    }
}
