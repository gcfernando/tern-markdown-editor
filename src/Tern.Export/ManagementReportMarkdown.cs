using System.Text;

namespace Tern.Export;

/// <summary>The complete management report in an editable Markdown hierarchy.</summary>
public static class ManagementReportMarkdown
{
    public static string Export(DecisionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var b = new StringBuilder("# ").Append(ReportText.Literal(report.Title)).Append("\n\n");
        b.Append("**Question:** ").Append(ReportText.Literal(report.Question)).Append("  \n**Date:** ").Append(ReportText.Literal(report.Date));
        if (report.PreparedBy.Length > 0) b.Append("  \n**Prepared by:** ").Append(ReportText.Literal(report.PreparedBy));
        if (report.DecisionBy.Length > 0) b.Append("  \n**").Append(report.HasDecision ? "Decision by" : "Needed by").Append(":** ").Append(ReportText.Literal(report.DecisionBy));
        if (report.Status.Length > 0) b.Append("  \n**Status:** ").Append(ReportText.Literal(report.Status));
        Section(b, report.HasDecision ? "Decision needed" : "Main question", report.Headline);
        Section(b, report.HasDecision ? "Recommendation" : "Key finding", report.Recommendation);
        if (report.RecommendationWhy.Length > 0) b.Append("\n\n").Append(ReportText.Literal(report.RecommendationWhy));
        Table(b, "Key figures", ["Measure", "Value", "Context"], report.Metrics.Select(m => new[] { m.Label, m.Value, m.Note }));
        Bullets(b, report.HasDecision ? "Reasons for the recommendation" : "What the notes show", report.Reasons);
        Bullets(b, "Conditions", report.SummaryConditions);
        Bullets(b, "Key risks", report.SummaryRisks);
        Section(b, "Next action", report.SummaryNextAction);

        b.Append("\n\n## Detailed findings\n");
        if (report.Findings.Count == 0)
            b.Append("\nNo detailed findings were supplied. Review the selected notes before sharing this report.\n");
        foreach (var f in report.Findings)
        {
            b.Append("\n### ").Append(ReportText.Literal(f.Title.Length > 0 ? f.Title : "Finding")).Append("\n\n**")
                .Append(ReportText.Literal(f.Kind)).Append(":** ").Append(ReportText.Highlight(f.Detail, f.Keywords));
            if (f.EvidenceIds.Count > 0) b.Append("\n\nEvidence: ").Append(string.Join(", ", f.EvidenceIds.Select(id => $"[E{id}]")));
            else b.Append("\n\n*No source-specific evidence was supplied for this finding.*");
            if (f.Derivation.Length > 0) b.Append("\n\n**Calculation and inputs:** ").Append(ReportText.Literal(f.Derivation));
            if (f.Limitations.Length > 0) b.Append("\n\n**Limitations:** ").Append(ReportText.Literal(f.Limitations));
            b.Append('\n');
        }
        Section(b, "Background", report.Background);
        Bullets(b, "What is on track", report.OnTrack);
        Bullets(b, "What needs attention", report.NeedsAttention);
        Table(b, "Timeline", ["When", "What"], report.Timeline.Select(t => new[] { t.When, t.What }));
        Table(b, "Options compared", ["Option", "Benefit", "Risk", "Cost", "Timing", "People"], report.Options.Select(o => new[]
            { o.Name + (o.Recommended ? " (recommended)" : ""), o.Benefit, o.Risk, o.Cost, o.Timing, o.Customers }));
        Table(b, "Risks in detail", ["Risk", "Likelihood", "Impact", "Action", "Owner", "Due"], report.Risks.Select(r => new[] { r.Risk, r.Likelihood, r.Impact, r.Action, r.Owner, r.Due }));
        Table(b, "Cost and effort", ["Item", "Amount", "Context"], report.Costs.Select(c => new[] { c.Item, c.Amount, c.Note }));

        b.Append("\n\n## Evidence you can check\n");
        if (report.Evidence.Count == 0) b.Append("\nNo source-specific evidence was supplied. This report must not be treated as verified.\n");
        foreach (var e in report.Evidence)
        {
            b.Append("\n### ").Append(e.Id > 0 ? $"E{e.Id}: " : "").Append(ReportText.Literal(e.Fact.Length > 0 ? e.Fact : "Source passage")).Append("\n\n");
            b.Append("**Note:** ").Append(ReportText.Literal(e.RelativePath.Length > 0 ? e.RelativePath : e.Note));
            if (e.SourceId > 0) b.Append(" · Source ").Append(e.SourceId);
            if (e.Section.Length > 0) b.Append("  \n**Section:** ").Append(ReportText.Literal(e.Section));
            if (e.Line is { } line) b.Append("  \n**Line:** ").Append(line);
            if (e.DocumentedDate.Length > 0) b.Append("  \n**Date written in the note:** ").Append(ReportText.Literal(e.DocumentedDate));
            if (e.QuoteVerified && e.Quote.Length > 0)
            {
                b.Append("\n\n");
                foreach (var lineText in e.Quote.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
                    b.Append("> ").Append(ReportText.Literal(lineText)).Append('\n');
            }
            else b.Append("\n\n*Source reference only. No exact quotation was verified for this claim.*\n");
        }
        b.Append("\n\n## Final assessment\n");
        if (report.FinalAssessment.Length > 0) b.Append('\n').Append(ReportText.Literal(report.FinalAssessment)).Append('\n');
        Table(b, "Next steps", ["Action", "Owner", "Due", "Status"], report.Actions.Select(a => new[] { a.Action, a.Owner, a.Due, a.Status }));
        Table(b, "Limitations and missing information", ["Missing information", "Why it matters", "Who can confirm"], report.Missing.Select(m => new[] { m.Item, m.WhyItMatters, m.WhoCanConfirm }));
        Section(b, "Reading coverage", report.Coverage);
        b.Append("\n\n## ").Append(report.HasDecision ? "Review and decision" : "Review and acknowledgement").Append("\n\nNo review or approval is implied. Complete these fields only after reviewing the full report.\n");
        if (report.HasDecision) b.Append("\n### Decision\n\n- [ ] Approve, subject to recorded conditions\n- [ ] Defer\n- [ ] Other\n\n### Conditions\n\n<br><br><br>\n");
        else b.Append("\n### Review notes\n\n<br><br><br>\n");
        Signature(b, report.HasDecision ? "Reviewed by" : "Prepared by");
        Signature(b, report.HasDecision ? "Decision authorized by" : "Reviewed by");
        return b.Append('\n').ToString();
    }

    private static void Signature(StringBuilder b, string label) => b.Append("\n### ").Append(label)
        .Append("\n\nSignature:\n\n<br><br><br>\n\nPrinted name:  \nTitle or role:  \nDate:\n");

    private static void Section(StringBuilder b, string title, string value)
    {
        if (value.Length > 0) b.Append("\n\n## ").Append(title).Append("\n\n").Append(ReportText.Literal(value));
    }

    private static void Bullets(StringBuilder b, string title, IReadOnlyList<string> values)
    {
        if (values.Count == 0) return;
        b.Append("\n\n## ").Append(title).Append("\n");
        foreach (var value in values) b.Append("\n- ").Append(ReportText.Literal(value));
    }

    private static void Table(StringBuilder b, string title, string[] headers, IEnumerable<string[]> values)
    {
        var rows = values.ToArray();
        if (rows.Length == 0) return;
        b.Append("\n\n## ").Append(title).Append("\n\n| ").Append(string.Join(" | ", headers)).Append(" |\n|")
            .Append(string.Concat(headers.Select(_ => " --- |")));
        foreach (var row in rows) b.Append("\n| ").Append(string.Join(" | ", row.Select(v => v.Length == 0 ? "Not in the notes" : ReportText.Literal(v).Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ')))).Append(" |");
    }
}

/// <summary>Plain report content never becomes executable HTML or model-supplied styling.</summary>
public static class ReportText
{
    public static string Literal(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("]", "\\]", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    public static string Highlight(string detail, IReadOnlyList<string> keywords)
    {
        var matches = new List<(int Start, int Length)>();
        foreach (var keyword in keywords.Where(k => k.Length is >= 2 and <= 80 && k.Length < detail.Length).OrderByDescending(k => k.Length))
            for (var offset = 0; offset < detail.Length;)
            {
                var index = detail.IndexOf(keyword, offset, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                if (!matches.Any(m => index < m.Start + m.Length && index + keyword.Length > m.Start)) matches.Add((index, keyword.Length));
                offset = index + keyword.Length;
            }
        var b = new StringBuilder();
        var at = 0;
        foreach (var match in matches.OrderBy(m => m.Start))
        {
            b.Append(Literal(detail[at..match.Start])).Append("**").Append(Literal(detail.Substring(match.Start, match.Length))).Append("**");
            at = match.Start + match.Length;
        }
        return b.Append(Literal(detail[at..])).ToString();
    }
}
