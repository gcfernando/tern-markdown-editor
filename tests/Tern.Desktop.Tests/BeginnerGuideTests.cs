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
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class BeginnerGuideTests
{
    private static readonly System.Text.Json.JsonSerializerOptions CaptureJsonOptions = new() { WriteIndented = true };
    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == name);
    private static Button Button(Window window, string content) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, content));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    private static void Settle(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

    [Fact]
    public void Everyday_lessons_cover_real_first_use_and_only_allow_explicit_safe_launches()
    {
        var registry = CommandRegistry.CreateDefault();
        var lessons = BeginnerLessons.For(registry);
        Assert.Equal(new[] { "write", "open", "url", "read", "format", "find", "save", "images", "pdf", "print", "recover", "practice", "organize", "ai-choice", "ai" }, lessons.Select(l => l.Id));
        Assert.All(lessons, lesson =>
        {
            Assert.False(string.IsNullOrWhiteSpace(lesson.When));
            Assert.False(string.IsNullOrWhiteSpace(lesson.Why));
            Assert.False(string.IsNullOrWhiteSpace(lesson.Prerequisite));
            Assert.False(string.IsNullOrWhiteSpace(lesson.Example));
            Assert.False(string.IsNullOrWhiteSpace(lesson.Result));
            Assert.False(string.IsNullOrWhiteSpace(lesson.Troubleshooting));
            Assert.True(lesson.Steps.Count >= 3);
            Assert.All(lesson.Steps, step => { Assert.False(string.IsNullOrWhiteSpace(step.Action)); Assert.False(string.IsNullOrWhiteSpace(step.Expected)); });
            if (lesson.LaunchCommandId is not null) Assert.True(FeatureCatalog.CanLaunchFromGuide(lesson.LaunchCommandId));
        });
        Assert.Contains("File → New Document", lessons[0].Steps[0].Action, StringComparison.Ordinal);
        Assert.Null(lessons.Single(l => l.Id == "save").LaunchCommandId);
        Assert.Null(lessons.Single(l => l.Id == "pdf").LaunchCommandId);
        Assert.Contains("Format → Bold", lessons.Single(l => l.Id == "format").Steps[1].Action, StringComparison.Ordinal);
        var url = lessons.Single(l => l.Id == "url");
        Assert.Contains("+ Add → Open from URL", url.Steps[0].Action, StringComparison.Ordinal);
        Assert.Contains("GitHub file link", url.Prerequisite, StringComparison.Ordinal);
        Assert.Contains("start that server first", url.Prerequisite, StringComparison.Ordinal);
        Assert.Contains("localhost, 127.0.0.1 or ::1", url.Prerequisite, StringComparison.Ordinal);
        Assert.Contains("sign-in-only", url.Troubleshooting, StringComparison.Ordinal);
        Assert.Contains("raw/download", url.Steps[1].Action, StringComparison.Ordinal);
        Assert.Contains("https://github.com/gcfernando/developer-guides-and-tools/blob/main/docs/data-science/machine-learning-handbook.md", url.Example, StringComparison.Ordinal);
        Assert.Contains("Nothing is sent merely by opening the panel", lessons.Single(l => l.Id == "ai").Why, StringComparison.Ordinal);
        Assert.True(registry.SetBinding(CommandIds.NewFile, "Ctrl+Alt+F10").Succeeded);
        Assert.Contains("Ctrl+Alt+F10", BeginnerLessons.For(registry)[0].Steps[0].Action, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Opens_on_a_concrete_first_note_lesson_and_navigation_never_edits_the_document()
    {
        using var fixture = new DesktopFixture();
        var document = fixture.Shell.NewDocument("Keep this writing");
        var revision = document.Session.Revision;
        var window = new HelpWindow(fixture.Shell.Registry);
        window.Show(fixture.Window); Settle(window);
        Assert.Equal(0, Find<TabControl>(window, "Guide sections").SelectedIndex);
        Assert.Equal("Write your first note", Find<TextBlock>(window, "Lesson title").Text);
        Assert.Contains("File → New Document", Find<TextBlock>(window, "Tutorial step").Text, StringComparison.Ordinal);
        Assert.Contains("# My first note", Find<SelectableTextBlock>(window, "Lesson example").Text, StringComparison.Ordinal);
        Click(Button(window, "Next"));
        Assert.Contains("Choose Split", Find<TextBlock>(window, "Tutorial step").Text, StringComparison.Ordinal);
        Click(Button(window, "Skip tutorial")); Assert.False(Button(window, "Next").IsEnabled);
        Click(Button(window, "Replay")); Assert.StartsWith("Step 1 of", Find<TextBlock>(window, "Tutorial step").Text, StringComparison.Ordinal);
        Assert.Equal(revision, document.Session.Revision); Assert.Equal("Keep this writing", document.CurrentText);
        Assert.Single(fixture.Shell.Tabs);
        window.Close();
    }

    [AvaloniaFact]
    public void Practice_illustration_and_recovery_lessons_are_readable_without_changing_work()
    {
        using var fixture = new DesktopFixture();
        var document = fixture.Shell.NewDocument("Keep my real note");
        var window = new HelpWindow(fixture.Shell.Registry);
        window.Show(fixture.Window); Settle(window);
        var goals = Find<ListBox>(window, "Choose what you want to do");
        goals.SelectedItem = goals.Items.OfType<BeginnerLesson>().Single(l => l.Id == "practice");
        Settle(window);
        Assert.Equal(BeginnerPractice.Text, Find<SelectableTextBlock>(window, "Lesson example").Text);
        Assert.True(Find<Grid>(window, "Source to preview illustration").IsEffectivelyVisible);
        goals.SelectedItem = goals.Items.OfType<BeginnerLesson>().Single(l => l.Id == "recover");
        Settle(window);
        Assert.False(Find<Grid>(window, "Source to preview illustration").IsVisible);
        window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "If something doesn't work")).IsExpanded = true;
        Settle(window);
        Assert.Contains("Recycle Bin", Find<TextBlock>(window, "Lesson troubleshooting").Text, StringComparison.Ordinal);
        Assert.Equal("Keep my real note", document.CurrentText);
        Assert.Single(fixture.Shell.Tabs);
        window.Close();
    }

    [AvaloniaFact]
    public void Search_reveals_complete_reference_and_Copilot_details_are_progressive()
    {
        using var fixture = new DesktopFixture();
        var window = new HelpWindow(fixture.Shell.Registry);
        window.Show(fixture.Window); Settle(window);
        Find<TextBox>(window, "Search feature help").Text = "Copilot"; Settle(window);
        Assert.Equal(1, Find<TabControl>(window, "Guide sections").SelectedIndex);
        var results = Find<ListBox>(window, "Feature search results");
        results.SelectedItem = results.Items.OfType<FeatureHelp>().Single(f => f.Id == "ai.copilot"); Settle(window);
        Assert.Contains("Edit → Settings → AI connections", Find<TextBlock>(window, "Tutorial step").Text, StringComparison.Ordinal);
        Assert.Contains("Copilot access", Find<TextBlock>(window, "Before you start").Text, StringComparison.Ordinal);
        var advanced = Find<Expander>(window, "Advanced feature details");
        Assert.True(advanced.IsVisible); Assert.False(advanced.IsExpanded);
        Assert.Contains("1.0.95", Assert.IsType<TextBlock>(advanced.Content).Text, StringComparison.Ordinal);
        Find<TextBox>(window, "Search feature help").Text = ""; Settle(window);
        Assert.Equal(FeatureCatalog.Search(fixture.Shell.Registry, null).Count, Find<ListBox>(window, "Feature search results").Items.Count);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(650, 680)]
    [InlineData(1080, 820)]
    public void Compact_and_wide_lessons_keep_the_actions_inside_the_window(double width, double height)
    {
        using var fixture = new DesktopFixture();
        var window = new HelpWindow(fixture.Shell.Registry) { Width = width, Height = height };
        window.Show(fixture.Window); Settle(window);
        foreach (var label in new[] { "Back", "Next", "Skip tutorial", "Replay", "Exit guide" })
        {
            var button = Button(window, label);
            var point = button.TranslatePoint(new Point(), window)!.Value;
            Assert.True(button.Bounds.Width > 0); Assert.True(button.Bounds.Height > 0);
            Assert.InRange(point.X, 0, window.Bounds.Width - button.Bounds.Width + 1);
            Assert.InRange(point.Y, 0, window.Bounds.Height - button.Bounds.Height + 1);
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Explicit_launch_keeps_instructions_open_and_coalesces_a_pending_open_action()
    {
        using var fixture = new DesktopFixture();
        var document = fixture.Shell.NewDocument("Keep my draft");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launched = new List<string>();
        var window = new HelpWindow(fixture.Shell.Registry, openFeature: id => { launched.Add(id); return gate.Task; });
        window.Show(fixture.Window); Settle(window);
        var launch = Find<Button>(window, "Open guided feature");
        Click(launch); Click(launch);
        Assert.Equal(CommandIds.NewFile, Assert.Single(launched));
        Assert.True(window.IsVisible); Assert.False(launch.IsEnabled);
        Assert.Equal("Keep my draft", document.CurrentText);
        gate.SetResult(); Settle(window);
        window.Close();
    }

    [AvaloniaFact]
    public void Every_goal_shows_action_example_result_and_recovery_without_running_it()
    {
        using var fixture = new DesktopFixture();
        var document = fixture.Shell.NewDocument("Original writing");
        var window = new HelpWindow(fixture.Shell.Registry);
        window.Show(fixture.Window); Settle(window);
        var goals = Find<ListBox>(window, "Choose what you want to do");
        foreach (var goal in goals.Items.OfType<BeginnerLesson>())
        {
            goals.SelectedItem = goal; Settle(window);
            Assert.Equal(goal.Name, Find<TextBlock>(window, "Lesson title").Text);
            Assert.Contains(goal.Steps[0].Action, Find<TextBlock>(window, "Tutorial step").Text, StringComparison.Ordinal);
            Assert.Equal(goal.Example, Find<SelectableTextBlock>(window, "Lesson example").Text);
            Assert.Contains(goal.Result, Find<TextBlock>(window, "Expected lesson result").Text, StringComparison.Ordinal);
            window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "If something doesn't work")).IsExpanded = true;
            Settle(window);
            Assert.Equal(goal.Troubleshooting, Find<TextBlock>(window, "Lesson troubleshooting").Text);
        }
        Assert.Equal("Original writing", document.CurrentText); Assert.Single(fixture.Shell.Tabs);
        window.Close();
    }

    [AvaloniaFact]
    public void Compact_reference_does_not_keep_an_inaccessible_wide_category_filter()
    {
        using var fixture = new DesktopFixture();
        var window = new HelpWindow(fixture.Shell.Registry);
        window.Show(fixture.Window); Settle(window);
        Find<TabControl>(window, "Guide sections").SelectedIndex = 1; Settle(window);
        Find<ComboBox>(window, "Feature categories").SelectedItem = "Format"; Settle(window);
        window.Width = 650; Settle(window);
        Find<TextBox>(window, "Search feature help").Text = "Save"; Settle(window);
        Assert.Contains(Find<ComboBox>(window, "Choose a matching feature").Items.OfType<FeatureHelp>(), f => f.Id == CommandIds.Save);
        window.Close();
    }

    [AvaloniaFact]
    public void Url_lesson_only_opens_the_tool_after_a_deliberate_click()
    {
        using var fixture = new DesktopFixture();
        var document = fixture.Shell.NewDocument("Keep this note");
        var launched = new List<string>();
        var window = new HelpWindow(fixture.Shell.Registry, openFeature: id => { launched.Add(id); return Task.CompletedTask; });
        window.Show(fixture.Window); Settle(window);
        var goals = Find<ListBox>(window, "Choose what you want to do");
        goals.SelectedItem = goals.Items.OfType<BeginnerLesson>().Single(l => l.Id == "url"); Settle(window);
        Assert.Empty(launched);
        Assert.Contains("File → Open from URL", Find<TextBlock>(window, "Tutorial step").Text, StringComparison.Ordinal);
        Assert.Contains("No connection", Find<TextBlock>(window, "Tutorial step").Text, StringComparison.Ordinal);
        Click(Find<Button>(window, "Open guided feature"));
        Assert.Equal(CommandIds.OpenUrl, Assert.Single(launched));
        Assert.True(window.IsVisible);
        Assert.Equal("Keep this note", document.CurrentText); Assert.Single(fixture.Shell.Tabs);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Capture_beginner_lessons_in_both_themes_and_compact_text_scale()
    {
        using var fixture = new DesktopFixture();
        var folder = Path.Combine(Screenshots.Folder, "beginner-guide");
        Directory.CreateDirectory(folder);
        foreach (var theme in new[] { ThemeChoice.Paper, ThemeChoice.Midnight })
        foreach (var compact in new[] { false, true })
        {
            fixture.Shell.Settings.Theme = theme;
            fixture.Shell.Settings.UiScale = compact ? 1.5 : 1;
            fixture.Window.ApplySettings(); fixture.Window.ApplyTheme();
            var window = new HelpWindow(fixture.Shell.Registry, openFeature: id => fixture.Window.RunCommandAsync(id)) { Width = compact ? 650 : 1080, Height = 820 };
            window.Show(fixture.Window); await Task.Delay(100); Settle(window);
            Assert.Contains("File → New Document", Find<TextBlock>(window, "Tutorial step").Text, StringComparison.Ordinal);
            using (var bitmap = window.CaptureRenderedFrame())
            {
                Assert.NotNull(bitmap);
                using var stream = File.Create(Path.Combine(folder, $"guide-{theme.ToString().ToLowerInvariant()}-{(compact ? "compact-150" : "normal")}.png"));
                bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            if (compact)
            {
                window.Width = 620; window.Height = 560;
                await Task.Delay(100); Settle(window);
                var instructions = Find<ScrollViewer>(window, "Lesson instructions");
                Assert.True(instructions.Bounds.Height >= 100, $"Instruction viewport was only {instructions.Bounds.Height} DIP");
                Assert.Equal(21, Find<TextBlock>(window, "Tutorial step").FontSize);
                var actionText = Find<TextBlock>(window, "Tutorial step");
                const string firstAction = "Choose File → New Document.";
                var actionEnd = actionText.Text!.IndexOf(firstAction, StringComparison.Ordinal) + firstAction.Length - 1;
                Assert.Contains(firstAction, actionText.Text, StringComparison.Ordinal);
                var actionBottom = actionText.TranslatePoint(new Point(), instructions)!.Value.Y;
                var foundActionLine = false;
                foreach (var line in actionText.TextLayout.TextLines)
                {
                    actionBottom += actionText.TextLayout.LineHeight > 0 ? actionText.TextLayout.LineHeight : line.Height;
                    if (line.FirstTextSourceIndex + line.Length > actionEnd) { foundActionLine = true; break; }
                }
                Assert.True(foundActionLine);
                Assert.InRange(actionBottom, 0, instructions.Bounds.Height + 1);
                Assert.True(Find<Button>(window, "Open guided feature").IsVisible);
                Assert.NotNull(Find<ComboBox>(window, "Choose a learning goal").SelectedItem);
                foreach (var button in new[] { Button(window, "Back"), Button(window, "Next"), Button(window, "Skip tutorial"), Button(window, "Replay"), Find<Button>(window, "Open guided feature"), Button(window, "Exit guide") })
                {
                    var point = button.TranslatePoint(new Point(), window)!.Value;
                    Assert.InRange(point.X, 0, window.Bounds.Width - button.Bounds.Width + 1);
                    Assert.InRange(point.Y, 0, window.Bounds.Height - button.Bounds.Height + 1);
                }
                var prefix = Path.Combine(folder, $"guide-{theme.ToString().ToLowerInvariant()}-minimum-150");
                using (var bitmap = window.CaptureRenderedFrame())
                {
                    Assert.NotNull(bitmap);
                    using var stream = File.Create(prefix + ".png");
                    bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
                await File.WriteAllTextAsync(prefix + ".json", System.Text.Json.JsonSerializer.Serialize(new
                {
                    width = window.Bounds.Width, height = window.Bounds.Height, textScale = fixture.Shell.Settings.UiScale,
                    instructionViewportHeight = instructions.Bounds.Height, stepFontSize = Find<TextBlock>(window, "Tutorial step").FontSize,
                    productionLaunchVisible = Find<Button>(window, "Open guided feature").IsVisible,
                    initialAction = firstAction, initialActionBottom = actionBottom,
                }, CaptureJsonOptions));
            }
            window.Close();
            if (!compact)
            {
                var reference = new HelpWindow(fixture.Shell.Registry, "ai.copilot", id => fixture.Window.RunCommandAsync(id));
                reference.Show(fixture.Window); await Task.Delay(100); Settle(reference);
                using var bitmap = reference.CaptureRenderedFrame(); Assert.NotNull(bitmap);
                using var stream = File.Create(Path.Combine(folder, $"copilot-reference-{theme.ToString().ToLowerInvariant()}.png"));
                bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                reference.Close();
            }
        }
        fixture.Shell.Settings.UiScale = 1;
        fixture.Window.ApplySettings();
    }
}
