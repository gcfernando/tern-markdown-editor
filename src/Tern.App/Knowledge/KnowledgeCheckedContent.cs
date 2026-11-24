using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Tern.Export;

namespace Tern.App.Knowledge;

public sealed record KnowledgeCachedNote(KnowledgeSource Source, string Text);
public sealed record KnowledgeCachedCoverage(int Read, int Total, int Notes, IReadOnlyList<string> NotRead);

/// <summary>One fully checked result. Every local format derives from this exact immutable report.</summary>
public sealed class KnowledgeCheckedContent
{
    public const int CheckPolicyVersion = 1;
    private readonly string _reportJson;
    public string Key { get; }
    public string Markdown { get; }
    public string Email { get; }
    public DecisionReport Report => JsonSerializer.Deserialize<DecisionReport>(_reportJson)!;

    private KnowledgeCheckedContent(DecisionReport report)
    {
        _reportJson = JsonSerializer.Serialize(report);
        Key = ReportKey(report);
        var snapshot = Report;
        Markdown = KnowledgeReportBuilder.ToMarkdown(snapshot);
        Email = KnowledgeReportEmail.Format(snapshot);
    }

    public static string ReportKey(DecisionReport report) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(report)));
    public static string SourceKey(IReadOnlyList<KnowledgeSource> sources, IReadOnlyList<KnowledgeCachedNote> notes) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        sources = sources.OrderBy(source => source.Id).Select(source => new { source.Id, source.WorkspaceRoot, source.Path, source.ContentDigest }),
        notes = notes.OrderBy(note => note.Source.Id).Select(note => new { note.Source.Id, digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(note.Text))) }),
    })));

    public static KnowledgeCheckedContent Create(DecisionReport report, IReadOnlyList<(KnowledgeSource Source, string Text)> notes, IReadOnlyList<string> remainingIssues)
    {
        if (remainingIssues.Count != 0) throw new InvalidOperationException("The report still needs checking. Correct its issues before sharing.");
        KnowledgeReportBuilder.ValidateManagementContent(report);
        KnowledgeReportBuilder.ValidateEvidence(report, notes);
        return new(report);
    }
}
