using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Tern.Export;

public sealed record ReportMetric(string Label, string Value, string Note);
public sealed record ReportTimelineItem(string When, string What);
public sealed record ReportOption(string Name, string Benefit, string Risk, string Cost, string Timing, string Customers, bool Recommended);
public sealed record ReportRisk(string Risk, string Likelihood, string Impact, string Action, string Owner, string Due);
public sealed record ReportCost(string Item, string Amount, string Note);
public sealed record ReportAction(string Action, string Owner, string Due, string Status);
public sealed record ReportMissing(string Item, string WhyItMatters, string WhoCanConfirm);
public sealed record ReportEvidence(string Fact, string Note, string Section)
{
    public int Id { get; init; }
    public int SourceId { get; init; }
    /// <summary>Verbatim text from the supplied note, never an AI paraphrase.</summary>
    public string Quote { get; init; } = "";
    public bool QuoteVerified { get; init; }
    public int? Line { get; init; }
    public string DocumentedDate { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string SourceDigest { get; init; } = "";
}

/// <summary>A substantive finding; its category distinguishes observation from a target or interpretation.</summary>
public sealed record ReportFinding(string Title, string Kind, string Detail)
{
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public IReadOnlyList<int> EvidenceIds { get; init; } = [];
    public string Derivation { get; init; } = "";
    public string Limitations { get; init; } = "";
}

/// <summary>The content of a report. The layout is fixed; sections with nothing to say are left out.</summary>
public sealed record DecisionReport
{
    public required string Title { get; init; }
    public required string Question { get; init; }
    public string PreparedBy { get; init; } = "";
    public required string Date { get; init; }
    public string DecisionBy { get; init; } = "";
    public string Status { get; init; } = "";
    /// <summary>False when the notes contain no decision: the report then shows a main question, key finding and next steps.</summary>
    public bool HasDecision { get; init; }
    public required string Headline { get; init; }
    public required string Recommendation { get; init; }
    public string RecommendationWhy { get; init; } = "";
    public IReadOnlyList<ReportMetric> Metrics { get; init; } = [];
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public string Background { get; init; } = "";
    public IReadOnlyList<string> OnTrack { get; init; } = [];
    public IReadOnlyList<string> NeedsAttention { get; init; } = [];
    public IReadOnlyList<ReportTimelineItem> Timeline { get; init; } = [];
    public IReadOnlyList<ReportOption> Options { get; init; } = [];
    public IReadOnlyList<ReportRisk> Risks { get; init; } = [];
    public IReadOnlyList<ReportCost> Costs { get; init; } = [];
    public IReadOnlyList<ReportAction> Actions { get; init; } = [];
    public IReadOnlyList<ReportMissing> Missing { get; init; } = [];
    public IReadOnlyList<ReportEvidence> Evidence { get; init; } = [];
    public IReadOnlyList<string> SummaryConditions { get; init; } = [];
    public IReadOnlyList<string> SummaryRisks { get; init; } = [];
    public string SummaryNextAction { get; init; } = "";
    public IReadOnlyList<ReportFinding> Findings { get; init; } = [];
    public string FinalAssessment { get; init; } = "";
    public string Coverage { get; init; } = "";
}

/// <summary>Lays out a <see cref="DecisionReport"/> as a professional A4 document that also reads well in black and white.</summary>
public static class DecisionReportPdf
{
    public static PdfExportResult Export(DecisionReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();
        SystemFontResolver.Install();
        using var pdf = new PdfDocument();
        pdf.Info.Title = ExportText.Clean(report.Title);
        pdf.Info.Author = ExportText.Clean(report.PreparedBy);
        pdf.Info.Creator = "Tern";
        pdf.Options.CompressContentStreams = true;
        using (var layout = new ManagementReportLayout(pdf, report, cancellationToken)) layout.Render();
        var pageCount = pdf.PageCount;
        using var stream = new MemoryStream();
        pdf.Save(stream, closeStream: false);
        cancellationToken.ThrowIfCancellationRequested();
        return new PdfExportResult(PdfUnicodeMap.Correct(stream.ToArray(), cancellationToken), pageCount, [], false);
    }

    public static PdfExportResult ExportToFile(DecisionReport report, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var result = Export(report, cancellationToken);
        SaveRendered(result, outputPath, cancellationToken);
        return result;
    }

    /// <summary>Commits a rendered report after the caller rechecks its current sources and task.</summary>
    public static void SaveRendered(PdfExportResult result, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        AtomicFile.Write(outputPath, result.Data, null, cancellationToken);
    }

}
