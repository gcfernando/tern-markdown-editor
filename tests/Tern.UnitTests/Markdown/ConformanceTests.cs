using System.Text.Json;
using Markdig;
using Tern.Markdown;
using Xunit.Abstractions;

namespace Tern.UnitTests.Markdown;

/// <summary>
/// Runs the official CommonMark 0.31.2 and GFM spec examples through the Tern pipelines. These test the parser
/// (Markdig) as configured by Tern; the application never displays this raw HTML without sanitising it.
/// </summary>
public class ConformanceTests(ITestOutputHelper output)
{
    private sealed record Example(int Number, string Section, string Markdown, string Html, string? Extension = null);

    private sealed record ApprovedDeviation(string Suite, int Number, string ExpectedHtml, string ActualHtml);

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static List<Example> LoadCommonMark()
    {
        using var stream = File.OpenRead(FixturePath("commonmark-0.31.2.json"));
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.EnumerateArray()
            .Select(e => new Example(e.GetProperty("example").GetInt32(), e.GetProperty("section").GetString()!, e.GetProperty("markdown").GetString()!, e.GetProperty("html").GetString()!))
            .ToList();
    }

    private static List<Example> LoadGfm()
    {
        var lines = File.ReadAllLines(FixturePath("gfm-spec.txt"));
        var examples = new List<Example>();
        var number = 0;
        var section = string.Empty;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith('#'))
            {
                section = lines[i].TrimStart('#', ' ');
            }

            if (!lines[i].StartsWith("```````````````````````````````` example", StringComparison.Ordinal))
            {
                continue;
            }

            var extension = lines[i]["```````````````````````````````` example".Length..].Trim();
            var markdown = new List<string>();
            var html = new List<string>();
            var target = markdown;
            for (i++; i < lines.Length && !lines[i].StartsWith("````````````````````````````````", StringComparison.Ordinal); i++)
            {
                if (lines[i] == ".")
                {
                    target = html;
                    continue;
                }

                target.Add(lines[i].Replace('→', '\t'));
            }

            number++;
            examples.Add(new Example(number, section, string.Concat(markdown.Select(l => l + "\n")), string.Concat(html.Select(l => l + "\n")), extension.Length == 0 ? null : extension));
        }

        return examples;
    }

    [Fact]
    public void CommonMark_pipeline_passes_the_official_spec_examples()
    {
        var examples = LoadCommonMark();
        var pipeline = MarkdownPipelines.For(MarkdownProfile.CommonMark);
        var failures = examples.Where(e => Markdig.Markdown.ToHtml(e.Markdown, pipeline) != e.Html).ToList();

        foreach (var f in failures)
        {
            output.WriteLine($"CommonMark #{f.Number} ({f.Section})");
        }

        output.WriteLine($"CommonMark: {examples.Count - failures.Count}/{examples.Count} pass");
        Assert.True(examples.Count >= 650, "fixture is truncated");

        AssertApprovedDeviations("CommonMark/CommonMark", examples, pipeline);
    }

    [Theory]
    [InlineData(MarkdownProfile.Gfm)]
    [InlineData(MarkdownProfile.Extended)]
    public void Gfm_profiles_pass_the_core_commonmark_examples_too(MarkdownProfile profile)
    {
        // Intentional extension changes are pinned individually as well as parser deviations.
        var examples = LoadCommonMark();
        var pipeline = MarkdownPipelines.For(profile);
        var passed = examples.Count(e => Markdig.Markdown.ToHtml(e.Markdown, pipeline) == e.Html);
        output.WriteLine($"{profile}: {passed}/{examples.Count}");
        AssertApprovedDeviations("CommonMark/" + profile, examples, pipeline);
    }

    [Fact]
    public void Gfm_extension_examples_report()
    {
        var pipeline = MarkdownPipelines.For(MarkdownProfile.Gfm);
        var ext = LoadGfm().Where(e => e.Extension is "table" or "strikethrough" or "tasklist" or "autolink").ToList();
        var failures = ext.Where(e => Markdig.Markdown.ToHtml(e.Markdown, pipeline) != e.Html).ToList();
        foreach (var f in failures)
        {
            output.WriteLine($"GFM #{f.Number} [{f.Extension}] {f.Section}\n--- md\n{f.Markdown}--- expected\n{f.Html}--- actual\n{Markdig.Markdown.ToHtml(f.Markdown, pipeline)}");
        }

        output.WriteLine($"GFM extensions: {ext.Count - failures.Count}/{ext.Count} pass");
        Assert.True(ext.Count > 20);

        AssertApprovedDeviations("GfmExtensions/Gfm", ext, pipeline);
    }

    private static void AssertApprovedDeviations(string suite, List<Example> examples, MarkdownPipeline pipeline)
    {
        var approved = JsonSerializer.Deserialize<List<ApprovedDeviation>>(File.ReadAllText(FixturePath("approved-deviations.json")))!;
        var expected = approved.Where(d => d.Suite == suite).OrderBy(d => d.Number).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Count, expected.Select(d => d.Number).Distinct().Count());
        var actual = examples.Select(e => new ApprovedDeviation(suite, e.Number, e.Html, Markdig.Markdown.ToHtml(e.Markdown, pipeline)))
            .Where(d => d.ExpectedHtml != d.ActualHtml).OrderBy(d => d.Number).ToList();
        // Pin both fixture IDs and exact expected/actual HTML. A new failure cannot hide
        // behind another example becoming conformant or an unchanged aggregate count.
        Assert.Equal(expected, actual);
    }
}
