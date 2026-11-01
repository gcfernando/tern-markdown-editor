using Tern.App.Commands;
using Tern.App.Help;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class CopilotGuidePromptTests
{
    [Theory]
    [InlineData("How to use AI for MD files")]
    [InlineData("How do I save my Markdown files?")]
    [InlineData("How can I configure GitHub Copilot?")]
    [InlineData("  Explain Markdown headings\nKeep this question exactly.  ")]
    public void Copilot_guide_requests_fit_the_limit_and_preserve_the_exact_question(string question)
    {
        var registry = CommandRegistry.CreateDefault();
        var message = GuideQuestionPrompt.Build(registry, question, forCopilot: true);
        Assert.InRange(message.Length, 1, CopilotTextClient.MaximumPromptCharacters);
        Assert.DoesNotContain("@", message, StringComparison.Ordinal);
        Assert.EndsWith("\n\nUSER QUESTION\n" + question, message, StringComparison.Ordinal);
        var features = FeatureCatalog.Search(registry, question);
        if (features.Count > 0) Assert.Contains(features[0].Name + ": " + features[0].Description, message, StringComparison.Ordinal);
    }

    [Fact]
    public void API_guide_requests_retain_the_complete_existing_command_reference_and_steps()
    {
        var registry = CommandRegistry.CreateDefault(); const string question = "How to use AI for MD files";
        var message = GuideQuestionPrompt.Build(registry, question, forCopilot: false);
        var reference = string.Join('\n', registry.Commands.Select(c => c.Title + " | " + c.Category + " | " + Gesture.Describe(registry.GetGesture(c.Id))));
        var relevant = string.Join('\n', FeatureCatalog.Search(registry, question).Take(8)
            .Select(f => f.Name + ": " + f.Description + " " + string.Join(" ", f.Steps)));
        Assert.Contains("\n\nTERN COMMANDS\n" + reference + "\n\nRELEVANT GUIDE\n" + relevant, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_questions_fail_locally_without_truncation()
    {
        Assert.Throws<InvalidOperationException>(() => GuideQuestionPrompt.Build(CommandRegistry.CreateDefault(),
            new string('q', CopilotTextClient.MaximumPromptCharacters), forCopilot: true));
    }
}
