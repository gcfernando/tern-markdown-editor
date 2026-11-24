using System.Text.Json;
using Tern.App.Knowledge;
using Tern.Export;

namespace Tern.App.Tests;

public sealed class ManagementEmailTests
{
    [Fact]
    public void Local_email_copies_checked_conditions_targets_and_all_figures_without_new_claims()
    {
        var report = Report() with
        {
            Recommendation = "The budget could be reserved after sponsor approval.",
            RecommendationWhy = "No purchase order has been issued.",
            Metrics = [new("Cash", "€2,400", "Subject to approval"), new("Staff effort", "Up to 40 hours", "24 available; 16 unallocated"), new("Response target", "6 hours", "Not a measured result")],
            SummaryConditions = ["Use synthetic requests only.", "Pause the pilot if personal data is entered."],
            SummaryRisks = ["No savings figure has been approved."],
            SummaryNextAction = "Ask the sponsor to decide.",
        };
        var before = JsonSerializer.Serialize(report);
        var email = KnowledgeReportEmail.Format(report);
        Assert.StartsWith("Subject: Service desk pilot\n\nDear leadership team,", email);
        foreach (var value in new[] { report.Recommendation, report.RecommendationWhy, report.SummaryNextAction }
            .Concat(report.SummaryConditions).Concat(report.SummaryRisks)) Assert.Contains(value, email);
        foreach (var metric in report.Metrics) { Assert.Contains(metric.Label + ": " + metric.Value, email); Assert.Contains(metric.Note, email); }
        Assert.DoesNotContain("immediate", email, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("established", email, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attached", email, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Kind regards,\nReport author", email);
        Assert.Equal(before, JsonSerializer.Serialize(report));
    }

    [Fact]
    public void Local_email_normalizes_header_newlines_and_does_not_invent_an_author()
    {
        var email = KnowledgeReportEmail.Format(Report() with { Title = "Pilot\r\nSubject: another line", PreparedBy = "" });
        Assert.StartsWith("Subject: Pilot Subject: another line\n\nDear leadership team,", email);
        Assert.EndsWith("Kind regards,", email);
        Assert.DoesNotContain("[Your name]", email);
    }

    [Fact]
    public void Local_email_keeps_long_checked_content_instead_of_silently_truncating_it()
    {
        var detail = new string('x', 10_000) + " END-CHECKED-CONTENT";
        Assert.Contains(detail, KnowledgeReportEmail.Format(Report() with { SummaryConditions = [detail] }));
    }

    [Fact]
    public void Local_email_retains_checked_staff_effort_when_it_was_not_repeated_in_metrics()
    {
        var email = KnowledgeReportEmail.Format(Report() with
        {
            Costs = [new("Training", "€800", "Only after approval")],
        });
        Assert.Contains("Up to 40 staff hours are needed; 16 remain unallocated.", email);
        Assert.Contains("Training: €800 — Only after approval", email);
    }

    [Fact]
    public void Request_uses_the_current_edited_report_and_separate_email_instructions()
    {
        var report = Report() with
        {
            Recommendation = "Reserve only after training and the 16-hour gap are resolved.",
            SummaryConditions = ["Training must be confirmed before launch."],
            SummaryRisks = ["Recurring charges remain unknown."],
            SummaryNextAction = "Ask the sponsor to decide after the conditions are resolved.",
        };
        var request = KnowledgeReportEmail.Request(report);
        Assert.Equal(AIPurpose.Email, request.Purpose);
        Assert.Contains("senior management", request.Instructions);
        Assert.Contains("copying text does not attach", request.Instructions);
        Assert.Contains(report.Recommendation, request.UserInput);
        Assert.Contains(report.SummaryConditions[0], request.UserInput);
        Assert.Contains(report.SummaryRisks[0], request.UserInput);
        Assert.Contains(report.SummaryNextAction, request.UserInput);
        Assert.Contains("staff effort separately", request.Instructions);
        Assert.Equal(request.Text.Length, request.CharacterCount);
        Assert.DoesNotContain("EARLIER CONVERSATION", request.Text);
    }

    [Fact]
    public void Valid_email_is_a_single_professional_management_memo()
    {
        var email = KnowledgeReportEmail.Read(JsonSerializer.Serialize(new
        {
            subject = "Decision requested: conditional pilot",
            body = "Please reserve up to €2,400, subject to approval. The 40 staff hours are separate; 16 remain unallocated. The full report provides the supporting evidence.",
        }), Report());
        Assert.StartsWith("Subject: Decision requested:", email);
        Assert.Contains("Dear leadership team,", email);
        Assert.EndsWith("Kind regards,\nReport author", email);
        Assert.DoesNotContain("Hi,", email);
    }

    [Theory]
    [InlineData("The attached report proves the benefits.")]
    [InlineData("The PDF has been attached.")]
    [InlineData("I have attached the full report.")]
    [InlineData("Please see the attachment.")]
    [InlineData("The expected saving is €99,999.")]
    [InlineData("<table><tr><td>Decision</td></tr></table>")]
    [InlineData("Dear leadership team,\nPlease reserve conditionally.")]
    [InlineData("Reserve conditionally.\n\nKind regards,\nReport author")]
    [InlineData("## Decision requested\nReserve conditionally.")]
    [InlineData("| Decision | Condition |\n| --- | --- |")]
    [InlineData("Reserve conditionally. [S1]")]
    public void Uncheckable_or_false_attachment_email_is_not_copied(string body)
    {
        var reply = JsonSerializer.Serialize(new { subject = "Decision requested", body });
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportEmail.Read(reply, Report()));
    }

    [Fact]
    public void Whole_email_check_includes_subject_body_and_current_reviewed_material()
    {
        var material = KnowledgeReportEmail.ExecutiveMaterial(Report());
        var email = "Subject: Current subject\n\nCurrent body with a condition.";
        var request = KnowledgeReportEmail.CheckRequest(material, email);
        Assert.Equal(AIPurpose.ReportCheck, request.Purpose);
        Assert.Contains("every factual claim", request.Instructions);
        Assert.Contains("Current subject", request.UserInput);
        Assert.Contains("Current body", request.UserInput);
        Assert.Contains("currentReviewedReport", request.UserInput);
        Assert.Contains("Report author", request.UserInput);
    }

    [Fact]
    public void Subject_field_must_not_add_a_second_subject_header()
    {
        var reply = JsonSerializer.Serialize(new { subject = "Subject: Decision requested", body = "Please reserve conditionally." });
        Assert.Throws<InvalidOperationException>(() => KnowledgeReportEmail.Read(reply, Report()));
    }

    private static DecisionReport Report() => new()
    {
        Title = "Service desk pilot", Question = "Should we reserve the pilot budget?", Date = "23 November 2026",
        PreparedBy = "Report author", HasDecision = true, Headline = "Reserve conditionally",
        Recommendation = "Reserve up to €2,400 subject to sponsor approval. Do not authorize launch yet.",
        Reasons = ["Up to 40 staff hours are needed; 16 remain unallocated."],
        Metrics = [new("Cash ceiling", "€2,400", "Excludes staff time")],
    };
}
