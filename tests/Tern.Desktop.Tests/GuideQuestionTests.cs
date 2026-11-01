using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Help;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class GuideQuestionTests
{
    [Fact]
    public void Save_help_preserves_selected_line_endings_and_only_new_notes_default_to_crlf()
    {
        var registry = CommandRegistry.CreateDefault();
        var features = FeatureCatalog.Search(registry, null);
        foreach (var id in new[] { CommandIds.Save, CommandIds.SaveAs, CommandIds.SaveAll, CommandIds.ChangeLineEnding, "editor.lineEndings" })
        {
            var feature = features.Single(f => f.Id == id);
            Assert.Contains("selected", feature.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("normalizes line breaks to CRLF", feature.Description, StringComparison.Ordinal);
            Assert.DoesNotContain("Every Save, Save As and Save All writes CRLF", feature.Description, StringComparison.Ordinal);
        }
        var save = BeginnerLessons.For(registry).Single(l => l.Id == "save");
        Assert.Contains("New notes default to CRLF", save.Why, StringComparison.Ordinal);
        Assert.Contains("LF, CRLF or CR", save.Steps[^1].Action, StringComparison.Ordinal);
        var tutorial = FeatureCatalog.Tutorials.Single(t => t.Name == "Change line endings safely");
        Assert.Contains(tutorial.Steps, step => step.Contains("Choose LF, CRLF or CR", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("How to use AI for MD files", "ai")]
    [InlineData("How do I save my Markdown files?", "save")]
    [InlineData("Can I make a PDF?", "pdf")]
    [InlineData("What are the benefits of connecting AI? What assistance can it provide, and what are the advantages and disadvantages with and without AI? How can configured AI help me create, modify and work with Markdown files?", "ai-choice")]
    public void Everyday_questions_find_existing_guide_features(string question, string lesson)
    {
        Assert.NotEmpty(FeatureCatalog.Search(CommandRegistry.CreateDefault(), question));
        Assert.Equal(lesson, GuideQuery.Lesson(question));
        Assert.Empty(FeatureCatalog.Search(CommandRegistry.CreateDefault(), "quantumplatypus"));
    }

    [Fact]
    public void Ai_choice_lesson_explains_real_workflow_tradeoffs_and_undoable_application()
    {
        var lesson = BeginnerLessons.For(CommandRegistry.CreateDefault()).Single(l => l.Id == "ai-choice");
        Assert.Contains("offline", lesson.Why, StringComparison.Ordinal);
        Assert.Contains("wrong", lesson.Why, StringComparison.Ordinal);
        Assert.Contains("Ollama", lesson.Prerequisite, StringComparison.Ordinal);
        Assert.Contains("need not use the internet", lesson.Prerequisite, StringComparison.Ordinal);
        Assert.Contains("charge", lesson.Troubleshooting, StringComparison.Ordinal);
        foreach (var prompt in new[] { "Draft:", "Rewrite:", "Explain:", "Check structure:" })
            Assert.Contains(prompt, lesson.Example, StringComparison.Ordinal);
        Assert.Contains("Share", FeatureCatalog.Search(CommandRegistry.CreateDefault(), "AI benefits").Single(f => f.Id == "ai.workflow").Steps[1], StringComparison.Ordinal);
        Assert.Contains("Undo", lesson.Steps[^1].Action, StringComparison.Ordinal);
        Assert.Contains("Split", lesson.Steps[^1].Action, StringComparison.Ordinal);
        Assert.Contains("File → Save", lesson.Steps[^1].Action, StringComparison.Ordinal);
        Assert.Contains("not automatically", lesson.Steps[^1].Expected, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Questions_show_steps_clear_stale_details_and_only_prefill_AI_on_explicit_click()
    {
        using var fixture = new DesktopFixture();
        var questions = new List<string>();
        var window = new HelpWindow(fixture.Shell.Registry, askAI: question => { questions.Add(question); return Task.CompletedTask; });
        try
        {
            window.Show(fixture.Window);
            var search = Find<TextBox>(window, "Search feature help");
            search.Text = "How to use AI for MD files";
            await Settle(window);
            Assert.Equal("Try optional AI help", Find<TextBlock>(window, "Lesson title").Text);
            Assert.Contains("Edit → Settings → AI connections", Find<TextBlock>(window, "Tutorial step").Text, StringComparison.Ordinal);
            Assert.Empty(questions);
            Capture(window, "ai-markdown-question");
            search.Text = "quantumplatypus";
            await Settle(window);
            Assert.Equal("Ask about anything", Find<TextBlock>(window, "Lesson title").Text);
            Assert.DoesNotContain("Print", Find<TextBlock>(window, "Tutorial step").Text, StringComparison.Ordinal);
            Assert.False(Find<Button>(window, "Open guided feature").IsVisible);
            Capture(window, "no-match-ai-fallback");
            Find<Button>(window, "Ask question").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("quantumplatypus", Assert.Single(questions));
            Assert.Empty(fixture.Shell.Tabs);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Standalone_guide_can_open_AI_with_the_exact_question_without_sending()
    {
        using var fixture = new DesktopFixture();
        var window = new HelpWindow(fixture.Shell.Registry);
        window.Show(fixture.Window);
        try
        {
            const string question = "Explain Markdown headings for a beginner";
            Find<TextBox>(window, "Search feature help").Text = question;
            await Settle(window);
            Find<Button>(window, "Ask question").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle(window);
            var ai = Assert.Single(window.OwnedWindows.OfType<AIConnectionsWindow>());
            try
            {
                Find<Button>(ai, "AI setup next").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Settle(ai);
                Find<TextBox>(ai, "Model name").Text = "synthetic-model";
                Find<TextBox>(ai, "API key (saved only when you choose Save connection)").Text = "synthetic-key";
                Find<Button>(ai, "AI setup next").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Settle(ai);
                Assert.Equal(question, Find<TextBox>(ai, "Exact message to send").Text);
            }
            finally { ai.Close(); }
        }
        finally { window.Close(); }
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == name);
    private static void Capture(Window window, string name)
    {
        var folder = Path.Combine(Screenshots.Folder, "guide-questions"); Directory.CreateDirectory(folder);
        using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
        using var stream = File.Create(Path.Combine(folder, name + ".png"));
        bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
    private static async Task Settle(Window window)
    { await Task.Delay(30, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
}
