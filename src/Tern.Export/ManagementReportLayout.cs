using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Tern.Export;

/// <summary>Flows the complete report without discarding long rows, findings or source passages.</summary>
internal sealed class ManagementReportLayout(PdfDocument pdf, DecisionReport report, CancellationToken token) : IDisposable
{
    private const double Left = 51, Right = 544.276, Top = 32, BodyTop = 82, Bottom = 783.89, Width = Right - Left;
    private static readonly XColor Navy = XColor.FromArgb(23, 50, 77), Ink = XColor.FromArgb(36, 54, 71), Muted = XColor.FromArgb(80, 99, 116),
        Teal = XColor.FromArgb(18, 109, 111), Pale = XColor.FromArgb(239, 249, 247), Line = XColor.FromArgb(211, 222, 228), Soft = XColor.FromArgb(246, 249, 251);
    private readonly PdfTypography _type = new(token);
    private readonly List<(string Label, string Text)> _summaryOverflow = [];
    private XGraphics? _graphics;
    private double _y;
    private string _context = "";
    private XGraphics G => _graphics!;

    public void Render()
    {
        NewPage();
        Summary();
        _context = "Detailed findings";
        NewPage();
        Section("Detailed findings");
        if (report.Findings.Count == 0) Paragraph("No detailed findings were supplied. Review the selected notes before sharing this report.", color: Muted);
        foreach (var finding in report.Findings)
        {
            Heading(finding.Title.Length > 0 ? finding.Title : "Finding");
            Paragraph(finding.Kind + ": " + finding.Detail, keywords: finding.Keywords, prefix: finding.Kind + ":");
            Paragraph(finding.EvidenceIds.Count > 0 ? "Evidence: " + string.Join(", ", finding.EvidenceIds.Select(id => $"[E{id}]"))
                : "No source-specific evidence was supplied for this finding.", size: 10.5, color: Muted);
            if (finding.Derivation.Length > 0) Paragraph("Calculation and inputs: " + finding.Derivation, prefix: "Calculation and inputs:");
            if (finding.Limitations.Length > 0) Paragraph("Limitations: " + finding.Limitations, prefix: "Limitations:");
        }
        foreach (var overflow in _summaryOverflow) { Heading(overflow.Label + " — full text"); Paragraph(overflow.Text); }
        Optional("Background", report.Background);
        Bullets("Supporting findings", report.Reasons);
        Bullets("Conditions", report.SummaryConditions);
        Bullets("Key risks", report.SummaryRisks);
        Optional("Next action", report.SummaryNextAction);
        Bullets("What is on track", report.OnTrack);
        Bullets("What needs attention", report.NeedsAttention);
        Grid("Key figures", ["Measure", "Value", "Context"], [.28, .20, .52], report.Metrics.Select(m => new[] { m.Label, m.Value, m.Note }));
        Grid("Timeline", ["When", "What"], [.24, .76], report.Timeline.Select(t => new[] { t.When, t.What }));
        if (report.HasDecision)
            Grid("Options compared", ["Option", "Benefit", "Risk", "Cost", "Timing", "People"], [.20, .18, .17, .15, .15, .15],
                report.Options.Select(o => new[] { o.Name + (o.Recommended ? " (recommended)" : ""), o.Benefit, o.Risk, o.Cost, o.Timing, o.Customers }));
        Grid("Risks in detail", ["Risk", "Likelihood", "Impact", "Action", "Owner", "Due"], [.24, .13, .13, .23, .15, .12],
            report.Risks.Select(r => new[] { r.Risk, r.Likelihood, r.Impact, r.Action, r.Owner, r.Due }));
        Grid("Cost and effort", ["Item", "Amount", "Context"], [.31, .21, .48], report.Costs.Select(c => new[] { c.Item, c.Amount, c.Note }));
        Evidence();
        KeepFinalAssessmentTogether();
        Section("Final assessment");
        if (report.FinalAssessment.Length > 0) Paragraph(report.FinalAssessment);
        Grid("Next steps", ["Action", "Owner", "Due", "Status"], [.45, .23, .15, .17], report.Actions.Select(a => new[] { a.Action, a.Owner, a.Due, a.Status }));
        Grid("Limitations and missing information", ["Missing information", "Why it matters", "Who can confirm"], [.34, .41, .25], report.Missing.Select(m => new[] { m.Item, m.WhyItMatters, m.WhoCanConfirm }));
        Optional("Reading coverage", report.Coverage);
        Review();
        _graphics?.Dispose(); _graphics = null;
        Footers();
    }

    private void NewPage()
    {
        token.ThrowIfCancellationRequested();
        _graphics?.Dispose();
        var page = pdf.AddPage(); page.Size = PdfSharp.PageSize.A4;
        _graphics = XGraphics.FromPdfPage(page); _y = BodyTop;
        DrawSingle(report.HasDecision ? "MANAGEMENT REPORT" : "INFORMATION REPORT", Left, Top, 8.5, true, Navy);
        var date = report.Date;
        var font = _type.Font(default, 8.5);
        var dateWidth = _type.Width(date, font);
        if (dateWidth < Width * .45) DrawSingle(date, Right - dateWidth, Top, 8.5, false, Muted);
        G.DrawLine(new XPen(Line, .8), Left, 60, Right, 60);
        // Section titles already introduce new detail/review pages. Repeat only the
        // source identity needed to understand a quotation continued onto a page.
        if (_context.StartsWith("Evidence E", StringComparison.Ordinal) || _context.StartsWith("Evidence ·", StringComparison.Ordinal))
            DrawSingle(_context, Left, 65, 8.5, false, Muted);
    }

    private void Need(double height)
    {
        if (_y + height > Bottom) NewPage();
    }

    private void DrawSingle(string value, double x, double y, double size, bool bold, XColor color)
    {
        var lines = Lines(value, Width, size, bold);
        if (lines.Count > 0) DrawLine(lines[0], x, y, color);
    }

    private void DrawLine(LineBox line, double x, double y, XColor color)
    {
        foreach (var item in line.Items)
            if (item.Font is not null && item.Atom.Text.Length > 0)
                G.DrawString(item.Atom.Text, item.Font, new XSolidBrush(color), x + item.X, y + line.Baseline);
    }

    private List<LineBox> Lines(string text, double width, double size = 11, bool bold = false, IReadOnlyList<string>? keywords = null, string? prefix = null)
    {
        var intervals = new List<(int Start, int End)>();
        foreach (var keyword in keywords ?? [])
            if (keyword.Length is >= 2 and <= 80 && keyword.Length < text.Length)
                for (var at = 0; at < text.Length;)
                {
                    var index = text.IndexOf(keyword, at, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) break;
                    intervals.Add((index, index + keyword.Length)); at = index + keyword.Length;
                }
        if (prefix is { Length: > 0 } && text.StartsWith(prefix, StringComparison.Ordinal)) intervals.Add((0, prefix.Length));
        var atoms = new List<Atom>();
        var word = new StringBuilder();
        var style = default(TextStyle);
        void Flush() { if (word.Length > 0) { atoms.Add(new(AtomKind.Word, word.ToString(), style)); word.Clear(); } }
        var offset = 0;
        foreach (var rune in ExportText.Clean(text).EnumerateRunes())
        {
            if (rune.Value == '\r') { offset++; continue; }
            if (rune.Value == '\n') { Flush(); atoms.Add(new(AtomKind.LineBreak, "", default)); offset++; continue; }
            if (Rune.IsWhiteSpace(rune)) { Flush(); atoms.Add(new(AtomKind.Space, " ", default)); offset += rune.Utf16SequenceLength; continue; }
            var requested = new TextStyle(bold || intervals.Any(i => offset >= i.Start && offset < i.End), false, false, false, false, false, 0, 0);
            var next = _type.GlyphStyle(rune.Value, requested, size);
            if (next != style) Flush();
            style = next; word.Append(rune.ToString()); offset += rune.Utf16SequenceLength;
        }
        Flush();
        return _type.Break(atoms, Math.Max(20, width), size, Bottom - BodyTop);
    }

    private void Paragraph(string text, double size = 11, bool bold = false, XColor? color = null, IReadOnlyList<string>? keywords = null, string? prefix = null, double indent = 0)
    {
        foreach (var line in Lines(text, Width - indent, size, bold, keywords, prefix))
        {
            token.ThrowIfCancellationRequested(); Need(line.Height);
            DrawLine(line, Left + indent, _y, color ?? Ink); _y += line.Height;
        }
        _y += 7;
    }

    private void Heading(string title)
    {
        var lines = Lines(title, Width, 13, true);
        Need(lines.Sum(l => l.Height) + 35); _y += 8;
        foreach (var line in lines) { Need(line.Height); DrawLine(line, Left, _y, Navy); _y += line.Height; }
        _y += 5;
    }

    private void Section(string title) { _context = title; Heading(title); }
    private void Optional(string title, string text) { if (text.Length > 0) { Heading(title); Paragraph(text); } }
    private void Bullets(string title, IReadOnlyList<string> values)
    {
        if (values.Count == 0) return;
        Heading(title);
        foreach (var value in values) Paragraph("• " + value);
    }

    private void RememberSummary(string label, string text)
    {
        var context = string.IsNullOrWhiteSpace(label) ? "Executive context" : label;
        if (!_summaryOverflow.Contains((context, text))) _summaryOverflow.Add((context, text));
    }

    private LineBox WithContinuation(LineBox line, double width, double size, bool bold)
    {
        var text = string.Concat(line.Items.Select(item => item.Atom.Text)).TrimEnd();
        while (true)
        {
            var marked = Lines(text + "…", width, size, bold);
            if (marked.Count == 1) return marked[0];
            if (text.Length == 0) return Lines("…", width, size, bold)[0];
            var elements = System.Globalization.StringInfo.ParseCombiningCharacters(text);
            text = text[..elements[^1]].TrimEnd();
        }
    }

    private void SummaryText(string label, string text, int preferredLines, double size = 11, bool bold = false, string? overflowLabel = null)
    {
        if (text.Length == 0) return;
        var lines = Lines(text, Width, size, bold);
        var context = overflowLabel ?? label;
        var bottom = _y + (label.Length > 0 ? 20 : 0);
        var count = 0;
        while (count < Math.Min(preferredLines, lines.Count) && bottom + lines[count].Height <= Bottom - 25)
            bottom += lines[count++].Height;
        if (count == 0) { RememberSummary(context, text); return; }
        var continues = count < lines.Count;
        if (continues) RememberSummary(context, text);
        if (label.Length > 0) { DrawSingle(label, Left, _y, 11.5, true, Navy); _y += 20; }
        for (var i = 0; i < count; i++)
        {
            var line = continues && i == count - 1 ? WithContinuation(lines[i], Width, size, bold) : lines[i];
            DrawLine(line, Left, _y, bold ? Navy : Ink); _y += line.Height;
        }
        _y += 8;
    }

    private void Summary()
    {
        SummaryText("", report.Title, 2, 23, true, "Report title");
        SummaryText("", report.Question, 2, overflowLabel: "Report question");
        var metadata = new List<string>();
        if (report.PreparedBy.Length > 0) metadata.Add("Prepared by " + report.PreparedBy);
        if (report.DecisionBy.Length > 0) metadata.Add((report.HasDecision ? "Decision by " : "Needed by ") + report.DecisionBy);
        if (report.Status.Length > 0) metadata.Add("Status: " + report.Status);
        if (metadata.Count > 0) SummaryText("", string.Join(" · ", metadata), 1, 9.5, overflowLabel: "Report details");
        DecisionPanel();
        MetricCards();
        ResourceStrip();
        SummaryText("Conditions", string.Join(" • ", report.SummaryConditions), 3);
        SummaryText("Key risks", string.Join(" • ", report.SummaryRisks), 2);
        SummaryText("Next action", report.SummaryNextAction.Length > 0 ? report.SummaryNextAction : report.Actions.FirstOrDefault()?.Action ?? "", 2);
        SummaryText(report.HasDecision ? "Reasons for the recommendation" : "What the notes show", string.Join(" • ", report.Reasons), 3);
        DrawSingle(_summaryOverflow.Count > 0
            ? "Some executive context continues in the detail pages. Review it before deciding."
            : "Detailed findings, complete figures, supporting passages and review fields follow.", Left, Bottom - 14, 9, false, Muted);
    }

    private List<LineBox> SummaryLines(string label, string text, double width, double size, int maximum, bool bold = false)
    {
        if (text.Length == 0) return [];
        var all = Lines(text, width, size, bold);
        var visible = all.Take(maximum).ToList();
        if (all.Count > maximum)
        {
            RememberSummary(label, text);
            if (visible.Count > 0) visible[^1] = WithContinuation(visible[^1], width, size, bold);
        }
        return visible;
    }

    private void DecisionPanel()
    {
        const double inset = 13;
        var head = SummaryLines(report.HasDecision ? "Decision needed" : "Main question", (report.HasDecision ? "" : "Main question: ") + report.Headline, Width - 2 * inset, 11, 2);
        var finding = SummaryLines(report.HasDecision ? "Recommendation" : "Key finding", report.Recommendation, Width - 2 * inset, 16, 3, true);
        var why = SummaryLines("Reason", report.RecommendationWhy, Width - 2 * inset, 11, 2);
        var height = 36 + head.Sum(l => l.Height) + finding.Sum(l => l.Height) + why.Sum(l => l.Height) + (why.Count > 0 ? 5 : 0);
        if (_y + height > Bottom - 55)
        {
            SummaryText(report.HasDecision ? "Decision needed" : "Main question", report.Headline, 2, 13, true);
            SummaryText(report.HasDecision ? "Recommendation" : "Key finding", report.Recommendation, 3, 13, true);
            SummaryText("", report.RecommendationWhy, 2, overflowLabel: "Reason for the recommendation");
            return;
        }
        G.DrawRectangle(new XPen(Teal, .8), new XSolidBrush(Pale), Left, _y, Width, height);
        var y = _y + 11;
        DrawSingle(report.HasDecision ? "DECISION REQUESTED" : "Key finding", Left + inset, y, 9, true, Teal); y += 17;
        foreach (var line in head) { DrawLine(line, Left + inset, y, Ink); y += line.Height; }
        foreach (var line in finding) { DrawLine(line, Left + inset, y, Navy); y += line.Height; }
        if (why.Count > 0) y += 5;
        foreach (var line in why) { DrawLine(line, Left + inset, y, Ink); y += line.Height; }
        _y += height + 12;
    }

    private void MetricCards()
    {
        var indices = CoverMetricIndices();
        var metrics = indices.Select(i => report.Metrics[i]).ToArray();
        if (metrics.Length == 0) return;
        const double gap = 9, inset = 10;
        var width = (Width - gap * (metrics.Length - 1)) / metrics.Length;
        var cards = metrics.Select(m => new
        {
            Value = SummaryLines("Figure: " + m.Label, m.Value, width - 2 * inset, 20, 2, true),
            Label = SummaryLines("Metric label", m.Label, width - 2 * inset, 10.5, 2, true),
            Note = SummaryLines("Figure context: " + m.Label, m.Note, width - 2 * inset, 9.5, 2),
        }).ToArray();
        var height = cards.Max(c => 24 + c.Value.Sum(l => l.Height) + c.Label.Sum(l => l.Height) + c.Note.Sum(l => l.Height));
        if (_y + height > Bottom - 50)
        {
            RememberSummary("Key figures", string.Join(" · ", report.Metrics.Select(m => m.Label + ": " + m.Value + " " + m.Note)));
            return;
        }
        for (var i = 0; i < cards.Length; i++)
        {
            var x = Left + i * (width + gap);
            G.DrawRectangle(new XPen(Line, .6), new XSolidBrush(Soft), x, _y, width, height);
            var y = _y + 8;
            foreach (var line in cards[i].Value) { DrawLine(line, x + inset, y, Navy); y += line.Height; }
            y += 4;
            foreach (var line in cards[i].Label) { DrawLine(line, x + inset, y, Ink); y += line.Height; }
            foreach (var line in cards[i].Note) { DrawLine(line, x + inset, y, Muted); y += line.Height; }
        }
        var selected = indices.ToHashSet();
        var additional = report.Metrics.Where((_, i) => !selected.Contains(i)).ToArray();
        if (additional.Length > 0) RememberSummary("Additional key figures", string.Join(" · ", additional.Select(m => m.Label + ": " + m.Value + " " + m.Note)));
        _y += height + 9;
    }

    private IReadOnlyList<int> CoverMetricIndices()
    {
        var all = Enumerable.Range(0, report.Metrics.Count).ToArray();
        if (all.Length <= 3) return all;
        var selected = new List<int>();
        bool HasLabel(int i, params string[] words) => MetricWords(report.Metrics[i].Label).Overlaps(words);
        bool HasTargetLabel(int i) => HasLabel(i, "target", "targets", "goal", "goals");
        bool HasTargetNote(int i) => MetricWords(report.Metrics[i].Note).Overlaps(["target", "targets", "goal", "goals"]);
        // Prefer an explicitly named cash/budget commitment over other financial
        // measures, then accept a currency-valued measure when its label is generic.
        var cash = all.FirstOrDefault(i => HasLabel(i, "cash", "budget", "funding", "funds"), -1);
        if (cash < 0) cash = all.FirstOrDefault(i => HasLabel(i, "cost", "costs", "price", "fee", "fees"), -1);
        if (cash < 0) cash = all.FirstOrDefault(i => report.Metrics[i].Value.Any(c => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.CurrencySymbol), -1);
        if (cash >= 0) selected.Add(cash);
        var target = all.FirstOrDefault(i => !selected.Contains(i) && HasTargetLabel(i), -1);
        if (target < 0) target = all.FirstOrDefault(i => !selected.Contains(i) && HasTargetNote(i), -1);
        if (target >= 0) selected.Add(target);
        if (target >= 0 && selected.Count < 3)
        {
            var measure = MetricWords(report.Metrics[target].Label);
            measure.ExceptWith(["target", "targets", "goal", "goals", "proposed", "planned", "desired", "expected", "required", "maximum", "minimum", "current", "baseline", "median", "mean", "average", "first", "last", "the", "a", "an", "of", "for", "to", "in"]);
            var baseline = all.Where(i => !selected.Contains(i) && !HasTargetLabel(i) && !HasTargetNote(i))
                .Select(i => new { Index = i, Overlap = MetricWords(report.Metrics[i].Label).Count(measure.Contains),
                    IsBaseline = MetricWords(report.Metrics[i].Label + " " + report.Metrics[i].Note).Overlaps(["baseline", "current", "measured", "review"]) })
                .Where(candidate => candidate.Overlap > 0).OrderByDescending(candidate => candidate.Overlap)
                .ThenByDescending(candidate => candidate.IsBaseline).ThenBy(candidate => candidate.Index).FirstOrDefault();
            if (baseline is not null) selected.Add(baseline.Index);
        }
        foreach (var i in all)
        {
            if (selected.Count == 3) break;
            if (!selected.Contains(i)) selected.Add(i);
        }
        // Preserve the report author's ordering among the selected facts; selection
        // changes no label, value, unit or target/result qualification.
        return selected.Order().ToArray();
    }

    private static HashSet<string> MetricWords(string value) => new(
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' '))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    private static bool IsResourceLabel(string value) => new[] { "staff", "effort", "labour", "labor", "resource", "hours" }
        .Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase));

    private void ResourceStrip()
    {
        var entries = report.Costs.Where(c => IsResourceLabel(c.Item)).Select(c => c.Item + ": " + c.Amount + (c.Note.Length > 0 ? " (" + c.Note + ")" : "")).ToArray();
        if (entries.Length == 0) entries = report.Metrics.Where(m => IsResourceLabel(m.Label)).Select(m => m.Label + ": " + m.Value + (m.Note.Length > 0 ? " (" + m.Note + ")" : "")).ToArray();
        if (entries.Length == 0) return;
        var text = string.Join(" · ", entries);
        var lines = SummaryLines("Staff and resource effort", text, Width - 18, 11, 2);
        var height = 12 + lines.Sum(l => l.Height);
        if (_y + height > Bottom - 40) { RememberSummary("Staff and resource effort", text); return; }
        G.DrawRectangle(new XSolidBrush(Pale), Left, _y, Width, height);
        var y = _y + 6;
        foreach (var line in lines) { DrawLine(line, Left + 9, y, Ink); y += line.Height; }
        _y += height + 8;
    }

    private void Grid(string title, string[] headers, double[] shares, IEnumerable<string[]> source)
    {
        var rows = source.ToArray(); if (rows.Length == 0) return;
        const double pad = 6;
        var widths = shares.Select(s => s * Width).ToArray();
        var head = headers.Select((h, i) => Lines(h, widths[i] - 2 * pad, 10.5, true)).ToArray();
        var headerHeight = head.Max(lines => lines.Sum(l => l.Height)) + 2 * pad;
        var firstRowHeight = 2 * pad + rows[0].Select((value, i) => Lines(value.Length > 0 ? value : "Not in the notes", widths[i] - 2 * pad).Count).Max() * 11 * PdfTypography.LineSpacing;
        var firstRowKeep = firstRowHeight + headerHeight + HeadingHeight(title) <= Bottom - BodyTop ? firstRowHeight : 11 * PdfTypography.LineSpacing + 2 * pad;
        Need(HeadingHeight(title) + headerHeight + firstRowKeep);
        Heading(title);
        void Header()
        {
            Need(headerHeight + 20);
            G.DrawRectangle(new XSolidBrush(XColor.FromArgb(234, 240, 244)), Left, _y, Width, headerHeight);
            var x = Left;
            for (var i = 0; i < head.Length; i++)
            {
                var y = _y + pad;
                foreach (var line in head[i]) { DrawLine(line, x + pad, y, Navy); y += line.Height; }
                x += widths[i];
            }
            _y += headerHeight;
        }
        Header();
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            token.ThrowIfCancellationRequested();
            var row = rows[rowIndex];
            if (row.Length != headers.Length) throw new ExportException("A report table has inconsistent columns.");
            var cells = row.Select((value, i) => Lines(value.Length > 0 ? value : "Not in the notes", widths[i] - 2 * pad)).ToArray();
            var count = cells.Max(c => c.Count); var start = 0;
            var completeHeight = count * 11 * PdfTypography.LineSpacing + 2 * pad;
            if (completeHeight + headerHeight <= Bottom - BodyTop && _y + completeHeight > Bottom) { NewPage(); Header(); }
            while (start < count)
            {
                var available = Math.Max(0, (int)((Bottom - _y - 2 * pad) / (11 * PdfTypography.LineSpacing)));
                if (available < 1) { NewPage(); Header(); continue; }
                var take = Math.Min(count - start, available);
                var height = take * 11 * PdfTypography.LineSpacing + 2 * pad;
                if (rowIndex % 2 == 1) G.DrawRectangle(new XSolidBrush(Soft), Left, _y, Width, height);
                var x = Left;
                for (var i = 0; i < cells.Length; i++)
                {
                    var y = _y + pad;
                    for (var line = start; line < Math.Min(start + take, cells[i].Count); line++) { DrawLine(cells[i][line], x + pad, y, Ink); y += 11 * PdfTypography.LineSpacing; }
                    x += widths[i];
                }
                _y += height; G.DrawLine(new XPen(Line, .6), Left, _y, Right, _y); start += take;
                if (start < count) { NewPage(); Header(); }
            }
        }
        _y += 7;
    }

    private void Evidence()
    {
        if (report.Evidence.Count > 0)
        {
            _context = "Evidence you can check";
            var first = report.Evidence[0];
            Need(Math.Min(Bottom - BodyTop, HeadingHeight("Evidence you can check") + EvidenceHeight(first)));
        }
        Section("Evidence you can check");
        if (report.Evidence.Count == 0) Paragraph("No source-specific evidence was supplied. This report must not be treated as verified.", color: Muted);
        foreach (var evidence in report.Evidence)
        {
            _context = (evidence.Id > 0 ? $"Evidence E{evidence.Id} · " : "Evidence · ") + evidence.Note;
            var blockHeight = EvidenceHeight(evidence);
            if (blockHeight <= Bottom - BodyTop) Need(blockHeight);
            else
            {
                // A truly long passage can continue, but its title/locator and first
                // quotation lines stay together. Continuation pages repeat identity.
                var firstLines = Lines(EvidencePassage(evidence), Width - 12).Take(2).Sum(l => l.Height) + 7;
                Need(Math.Min(Bottom - BodyTop, HeadingHeight(EvidenceTitle(evidence)) + EvidenceLocatorHeight(evidence) + firstLines));
            }
            Heading(EvidenceTitle(evidence));
            Paragraph(EvidenceLocator(evidence), size: 10.5, color: Muted);
            if (evidence.DocumentedDate.Length > 0) Paragraph("Date written in the note: " + evidence.DocumentedDate, size: 10.5, color: Muted);
            if (evidence.QuoteVerified && evidence.Quote.Length > 0) Paragraph("“" + evidence.Quote + "”", indent: 12);
            else Paragraph("Source reference only. No exact quotation was verified for this claim.", color: Muted);
        }
    }

    private double HeadingHeight(string title) => 13 + Lines(title, Width, 13, true).Sum(l => l.Height);
    private double ParagraphHeight(string text, double size = 11, double indent = 0) => Lines(text, Width - indent, size).Sum(l => l.Height) + 7;
    private static string EvidenceTitle(ReportEvidence evidence) => (evidence.Id > 0 ? $"E{evidence.Id}: " : "") + (evidence.Fact.Length > 0 ? evidence.Fact : "Source passage");
    private static string EvidenceLocator(ReportEvidence evidence) => "Note: " + (evidence.RelativePath.Length > 0 ? evidence.RelativePath : evidence.Note)
        + (evidence.SourceId > 0 ? " · Source " + evidence.SourceId : "") + (evidence.Section.Length > 0 ? " · Section: " + evidence.Section : "")
        + (evidence.Line is { } line ? " · Line " + line : "");
    private static string EvidencePassage(ReportEvidence evidence) => evidence.QuoteVerified && evidence.Quote.Length > 0
        ? "“" + evidence.Quote + "”" : "Source reference only. No exact quotation was verified for this claim.";
    private double EvidenceLocatorHeight(ReportEvidence evidence) => ParagraphHeight(EvidenceLocator(evidence), 10.5)
        + (evidence.DocumentedDate.Length > 0 ? ParagraphHeight("Date written in the note: " + evidence.DocumentedDate, 10.5) : 0);
    private double EvidenceHeight(ReportEvidence evidence) => HeadingHeight(EvidenceTitle(evidence)) + EvidenceLocatorHeight(evidence)
        + ParagraphHeight(EvidencePassage(evidence), indent: evidence.QuoteVerified && evidence.Quote.Length > 0 ? 12 : 0);

    private double GridHeight(string title, string[] headers, double[] shares, IEnumerable<string[]> source)
    {
        var rows = source.ToArray(); if (rows.Length == 0) return 0;
        var widths = shares.Select(s => s * Width - 12).ToArray();
        var header = 12 + headers.Select((h, i) => Lines(h, widths[i], 10.5, true).Sum(l => l.Height)).Max();
        var body = rows.Sum(row => 12 + row.Select((value, i) => Lines(value.Length > 0 ? value : "Not in the notes", widths[i]).Count).Max() * 11 * PdfTypography.LineSpacing);
        return HeadingHeight(title) + header + body + 7;
    }

    private void KeepFinalAssessmentTogether()
    {
        _context = "Final assessment";
        var height = HeadingHeight("Final assessment")
            + (report.FinalAssessment.Length > 0 ? ParagraphHeight(report.FinalAssessment) : 0)
            + GridHeight("Next steps", ["Action", "Owner", "Due", "Status"], [.45, .23, .15, .17], report.Actions.Select(a => new[] { a.Action, a.Owner, a.Due, a.Status }))
            + GridHeight("Limitations and missing information", ["Missing information", "Why it matters", "Who can confirm"], [.34, .41, .25], report.Missing.Select(m => new[] { m.Item, m.WhyItMatters, m.WhoCanConfirm }))
            + (report.Coverage.Length > 0 ? HeadingHeight("Reading coverage") + ParagraphHeight(report.Coverage) : 0);
        // Only force a complete group when it fits a page. Larger final sections
        // retain normal flow and repeated table headers, without shrinking content.
        if (height <= Bottom - BodyTop) Need(height);
        else Need(HeadingHeight("Final assessment") + 2 * 11 * PdfTypography.LineSpacing);
    }

    private void Review()
    {
        _context = report.HasDecision ? "Review and decision" : "Review and acknowledgement";
        NewPage(); Section(_context);
        Paragraph("No review or approval is implied. Complete these fields only after reviewing the full report.");
        Heading(report.HasDecision ? "Decision" : "Review notes");
        if (report.HasDecision)
        {
            foreach (var value in new[] { "Approve, subject to recorded conditions", "Defer", "Other" })
            {
                G.DrawRectangle(new XPen(Navy, .7), Left, _y + 3, 10, 10);
                Paragraph(value, indent: 18);
            }
            Heading("Conditions");
        }
        for (var i = 0; i < 3; i++) { _y += 25; G.DrawLine(new XPen(Line, .7), Left, _y, Right, _y); }
        _y += 13;
        Signature(report.HasDecision ? "Reviewed by" : "Prepared by");
        Signature(report.HasDecision ? "Decision authorized by" : "Reviewed by");
    }

    private void Signature(string title)
    {
        Need(155); Heading(title);
        _y += 60; G.DrawLine(new XPen(Navy, .7), Left, _y, Right, _y);
        DrawSingle("Signature", Left, _y + 3, 9, false, Muted); _y += 33;
        foreach (var (x, end, label) in new[] { (Left, Left + 210, "Printed name"), (Left + 228, Right - 103, "Title or role"), (Right - 85, Right, "Date") })
        {
            DrawSingle(label, x, _y, 9, false, Muted); G.DrawLine(new XPen(Line, .7), x, _y + 27, end, _y + 27);
        }
        _y += 42;
    }

    private void Footers()
    {
        for (var i = 0; i < pdf.PageCount; i++)
        {
            using var graphics = XGraphics.FromPdfPage(pdf.Pages[i], XGraphicsPdfPageOptions.Append);
            var font = _type.Font(default, 8.5);
            graphics.DrawLine(new XPen(Line, .6), Left, 801, Right, 801);
            graphics.DrawString("AI-assisted report · Sources listed in evidence", font, new XSolidBrush(Muted), Left, 816);
            var label = $"{i + 1} / {pdf.PageCount}";
            graphics.DrawString(label, font, new XSolidBrush(Muted), Right - graphics.MeasureString(label, font).Width, 816);
        }
    }

    public void Dispose() { _graphics?.Dispose(); _type.Dispose(); }
}
