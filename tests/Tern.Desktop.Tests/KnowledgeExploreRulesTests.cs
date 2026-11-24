using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Tern.App.Knowledge;
using Tern.Export;

namespace Tern.Desktop.Tests;

/// <summary>Plain-language rules for the Explore your notes screens.</summary>
public sealed partial class AIWritingWorkflowTests
{
    [AvaloniaFact]
    public async Task Offline_mode_blocks_ask_before_any_search_even_with_no_ai_connection_and_clears_when_turned_off()
    {
        var calls = 0;
        using var f = Fixture((_, _) => { calls++; return Task.FromResult(Models()); });
        var path = await f.WriteAsync("notes/launch.md", "# Launch\n\nThe launch is in April.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Shell.Settings.OfflineMode = true;
        f.Window.ShowKnowledgeAssistant(); Pump(f.Window);
        var panel = Assert.Single(KnowledgePanels(f.Window));
        var find = Find<Button>(panel, "Find my notes");
        Assert.False(find.IsEnabled);
        Assert.Contains("turn off Offline mode", Find<TextBlock>(panel, "Help for your question").Text);
        Find<TextBox>(panel, "Your question").Text = "When is the launch?";
        Assert.False(find.IsEnabled);
        Assert.False(Find<Button>(panel, "Search with my words").IsEnabled);
        Click(find); Pump(f.Window);
        Assert.False(Find<Button>(panel, "Get my answer").IsVisible);
        Assert.Equal(0, calls);
        f.Shell.Settings.OfflineMode = false;
        panel.RefreshSettings();
        Assert.True(find.IsEnabled);
        Assert.False(Find<Button>(panel, "Open Settings").IsVisible);
    }

    [Theory]
    [InlineData("LICENSE.md", "When is the launch?", true)]
    [InlineData("licenses/MIT.md", "When is the launch?", true)]
    [InlineData("tests/launch.md", "When is the launch?", true)]
    [InlineData("launch_sample.md", "When is the launch?", true)]
    [InlineData("example-launch.md", "When is the launch?", true)]
    [InlineData("launch_sample.md", "Show the sample launch", false)]
    [InlineData("LICENSE.md", "What does the license say?", false)]
    [InlineData("REPOSITORY_STANDARDS.md", "What are the standards?", false)]
    [InlineData("contest-notes.md", "Who won the contest?", false)]
    public void Results_hide_license_test_and_sample_files_unless_asked(string file, string question, bool hidden) =>
        Assert.Equal(hidden, KnowledgeRetrieval.IsHiddenResult(file, question));

    [AvaloniaTheory]
    [InlineData(50_000)]
    [InlineData(80_000)]
    public async Task Not_found_screen_has_one_question_and_one_main_button(int limit)
    {
        var sent = new List<string>();
        using var f = new DesktopFixture(directory => TwoLimits(directory, _ => Reply("""{"questions":[]}"""), sent));
        f.Window.AIWorkspace.Select(limit == 50_000 ? FirstId : SecondId);
        var path = await f.WriteAsync("notes/launch.md", "# Launch\n\nThe launch team meets weekly.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant(); Pump(f.Window);
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Find<TextBox>(panel, "Your question").Text = "zebra";
        Click(Find<Button>(panel, "Find my notes"));
        await Until(() => Find<TextBlock>(panel, "Why we could not find it").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        Pump(f.Window);
        var main = Assert.Single(panel.GetLogicalDescendants().OfType<Button>(), button => button.IsEffectivelyVisible && button.Classes.Contains("accent"));
        Assert.Equal("Change my question", main.Content);
        Assert.True(main.IsDefault);
        Assert.False(Find<Button>(panel, "Ask a new question").IsVisible);
        Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "What would you like to try next?");
        Assert.NotEmpty(sent);
        Assert.Contains(sent, message => message.StartsWith("TERN INSTRUCTIONS (Recovery)", StringComparison.Ordinal)
            && message.Contains("zebra", StringComparison.Ordinal));
        Assert.All(sent, message => Assert.InRange(message.Length, 1, limit));
    }

    [AvaloniaFact]
    public async Task File_names_keep_underscores_in_note_rows_and_accessible_names()
    {
        using var f = Fixture((_, _) => Task.FromResult(Models()));
        var path = await f.WriteAsync("notes/REPOSITORY_STANDARDS.md", "# Standards\n\nThe repository standards need a review.");
        Assert.True(await f.Shell.PromoteFolderAsync(Path.GetDirectoryName(path)!));
        f.Shell.Settings.KnowledgeAllowRemote = true;
        f.Window.ShowKnowledgeAssistant(); Pump(f.Window);
        var panel = Assert.Single(KnowledgePanels(f.Window));
        Find<TextBox>(panel, "Your question").Text = "What do the repository standards need?";
        Click(Find<Button>(panel, "Find my notes"));
        await Until(() => Find<Button>(panel, "Get my answer").IsEffectivelyVisible && !Find<Button>(panel, "Cancel").IsEnabled);
        Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "REPOSITORY_STANDARDS");
        Assert.Contains(panel.GetLogicalDescendants().OfType<CheckBox>(), box => AutomationProperties.GetName(box) == "Include note: REPOSITORY_STANDARDS.md");
    }

    [AvaloniaFact]
    public void Recent_file_menu_keeps_underscores_in_the_visible_name()
    {
        using var f = Fixture((_, _) => Task.FromResult(Models()));
        var file = Path.Combine(f.Docs, "REPOSITORY_STANDARDS.md");
        File.WriteAllText(file, "# Standards");
        f.Shell.Recent.AddFile(file);
        var menu = f.Window.FindControl<MenuItem>("OpenRecentMenu")!;
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
        var item = Assert.Single(menu.Items.OfType<MenuItem>(), entry => entry.Tag is Tern.Workspaces.RecentEntry recent && recent.Path == file);
        Assert.Equal("REPOSITORY_STANDARDS.md", Assert.IsType<TextBlock>(item.Header).Text);
    }

    [AvaloniaFact]
    public async Task Report_review_uses_plain_words_and_names_the_printer_before_printing()
    {
        var (f, panel) = await OpenTwoNotes();
        using (f)
        {
            panel.DefaultPrinterName = () => "Office_Printer";
            panel.PrintPdf = (_, _, _) => Task.CompletedTask;
            await GetAnswer(panel);
            await PrepareReport(panel, "Print");
            var words = panel.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text ?? "").ToArray();
            Assert.Contains(words, text => text.Contains("1 fact from your notes", StringComparison.Ordinal));
            Assert.DoesNotContain(words, text => text.Contains("sourced", StringComparison.OrdinalIgnoreCase));
            var printer = Find<TextBlock>(panel, "Report printer");
            Assert.True(printer.IsEffectivelyVisible);
            Assert.Equal("Your printer: Office_Printer", printer.Text);
        }
    }

    [Fact]
    public void Note_report_has_blank_review_and_decision_fields()
    {
        var report = new DecisionReport { Title = "Launch", Question = "When?", Date = "1 May 2026", Headline = "Keep the date?", Recommendation = "Keep it", HasDecision = true, PreparedBy = "Sam" };
        var markdown = KnowledgeReportBuilder.ToMarkdown(report);
        Assert.Contains("## Review and decision", markdown);
        Assert.Contains("**Prepared by:** Sam", markdown);
        Assert.Contains("### Decision authorized by", markdown);
        Assert.Contains("Signature:\n\n<br><br><br>\n\nPrinted name:", markdown);
        Assert.Contains("Title or role:", markdown);
        Assert.Contains("No review or approval is implied", markdown);
        Assert.DoesNotContain("A person reviewed it", markdown);
    }
}
