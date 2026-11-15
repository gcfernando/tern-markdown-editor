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
using Tern.App.Settings;
using Tern.Desktop.Services;
using Tern.Desktop.Views;
using Tern.Markdown;

namespace Tern.Desktop.Tests;

public sealed class Release104Tests
{
    [Theory]
    [InlineData("I need to add a new AI connection, how can I do that", "connection")]
    [InlineData("My search results are not highlighted in reading mode", "search")]
    [InlineData("Why does exporting my PDF change its appearance?", "export")]
    [InlineData("How can I change the theme of Tern?", "theme")]
    public void Conversational_questions_retrieve_grounded_help(string question, string expected)
    {
        var registry = CommandRegistry.CreateDefault();
        Assert.True(GuideQuestionPrompt.CanAnswer(registry, question));
        Assert.Contains(expected, GuideQuestionPrompt.OfflineAnswer(registry, question), StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(question, GuideQuestionPrompt.Build(registry, question, true));
        Assert.NotEmpty(GuideQuestionPrompt.Relevant(registry, question));
    }

    [AvaloniaFact]
    public async Task Missing_provider_automatically_answers_the_reported_connection_question_offline()
    {
        using var f = new DesktopFixture();
        using var tutor = new HelpTutorView(f.Window.AIWorkspace, f.Shell.Registry, () => Task.CompletedTask, null, null, _ => { }, null);
        var window = new Window { Content = tutor, Width = 900, Height = 760 }; window.Show(f.Window); Pump(window);
        try
        {
            tutor.Question.Text = "I need to add a new AI connection, how can I do that";
            tutor.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window);
            for (var i = 0; i < 100 && !tutor.Transcript.IsVisible; i++) { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(window); }
            Assert.True(tutor.Transcript.IsVisible);
            Assert.Contains("offline answer", tutor.Transcript.Text);
            Assert.Contains("connection", tutor.Transcript.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(CommandIds.AIConnections, GuideQuestionPrompt.Relevant(f.Shell.Registry, tutor.Question.Text!).First().Id);
            Assert.Contains("Choose service", tutor.Transcript.Text);
            Assert.Contains("add a new AI connection", tutor.Question.Text);
            Capture(window, "help-offline-recovery");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Midnight)]
    [InlineData(ThemeChoice.Paper)]
    public async Task Export_completion_actions_stay_outside_the_form_scrollbar(ThemeChoice theme)
    {
        using var f = new DesktopFixture();
        f.Shell.Settings.Theme = theme; f.Window.ApplyTheme();
        var window = new ExportWindow("sample.md", null, "# Example\n\n```python\nprint('hello')\n```", MarkdownProfile.Extended, null, ExportFormat.Html);
        window.Show(f.Window); Pump(window);
        try
        {
            var body = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Content is StackPanel);
            var before = body.Extent.Height;
            Assert.True(await window.ExportAsync(Path.Combine(f.Docs, new string('a', 120) + ".html"))); Pump(window);
            var open = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Open file"));
            Assert.True(open.IsEffectivelyVisible);
            Assert.DoesNotContain(open.GetVisualAncestors(), a => a == body);
            Assert.InRange(Math.Abs(body.Extent.Height - before), 0, 1);
            Assert.Contains("Export complete", window.StatusText);
            Assert.DoesNotContain(f.Docs, window.StatusText);
            Assert.True(open.TranslatePoint(default, window)!.Value.Y + open.Bounds.Height <= window.ClientSize.Height);
            Capture(window, "export-complete-" + theme);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Updated_tutorials_cover_search_export_print_and_recovery()
    {
        var names = FeatureCatalog.TutorialsFor(CommandRegistry.CreateDefault()).Select(t => t.Name).ToArray();
        Assert.Contains("Find and reveal matches in every mode", names);
        Assert.Contains("Export a formatted copy", names);
        Assert.Contains("Review and print a captured document", names);
        Assert.Contains("Ask about Tern and recover from connection errors", names);
        Assert.Contains(FeatureCatalog.Search(CommandRegistry.CreateDefault(), "recover connection"), f => f.Id.StartsWith("tutorial.1.0.4.", StringComparison.Ordinal));
    }

    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Capture(Window window, string name)
    {
        var folder = Path.Combine(Screenshots.Folder, "v1.0.4"); Directory.CreateDirectory(folder);
        using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
        using var stream = File.Create(Path.Combine(folder, name + ".png")); bitmap.Save(stream);
    }
}
