using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Settings;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class SixScreenIssueTests
{
    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper, 1d)]
    [InlineData(ThemeChoice.Midnight, 1.5d)]
    [InlineData(ThemeChoice.Paper, 2d)]
    public async Task Answer_gets_more_height_after_rendering_and_question_stays_available(ThemeChoice theme, double scale)
    {
        using var f = new DesktopFixture(); SetTheme(f, theme, scale);
        using var tutor = new HelpTutorView(f.Window.AIWorkspace, f.Shell.Registry, () => Task.CompletedTask, null, null, _ => { }, null);
        var window = new Window { Width = 1080, Height = 900, Content = tutor }; window.Show(f.Window); Pump(window);
        try
        {
            Assert.Equal(160, tutor.Question.MinHeight);
            var initialHeight = tutor.Question.Bounds.Height;
            await tutor.Transcript.ShowAsync("# Your answer\n\n" + string.Join("\n\n", Enumerable.Repeat("Use File → Print preview, then open the printable PDF to inspect pages.", 20)), TestContext.Current.CancellationToken, () => true);
            Pump(window);
            Assert.True(tutor.Question.IsEffectivelyVisible);
            Assert.True(tutor.Question.Bounds.Height < initialHeight);
            Assert.True(tutor.Transcript.Bounds.Height >= 220, $"Answer height: {tutor.Transcript.Bounds.Height}");
            Capture(window, $"answer-{theme}-{scale}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void One_mouse_click_accepts_the_clicked_picker_row()
    {
        var picker = new QuickPicker();
        var window = new Window { Width = 900, Height = 600, Content = picker };
        window.Show();
        var accepted = new List<PickerItem>();
        picker.Open("Choose line ending", "", _ => [new("CRLF (Windows)", "Convert now; keep when saving", "", "crlf"), new("LF (Unix / macOS)", "Convert now; keep when saving", "", "lf")], accepted.Add, () => { }); Pump(window);
        try
        {
            var row = picker.GetVisualDescendants().OfType<ListBoxItem>().Single(c => c.Content is PickerItem { Payload: "lf" });
            var point = row.TranslatePoint(new Point(30, row.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump(window);
            Assert.Equal("lf", Assert.Single(accepted).Payload);
            Capture(window, "single-click-picker");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_content_is_removed_for_every_scope_and_raw_request_ui_is_removed(bool selection)
    {
        using var f = new DesktopFixture();
        var scope = new ComboBox { ItemsSource = selection ? new[] { "Selected text", "Entire note", "Instruction only" } : new[] { "Entire note", "Instruction only" }, SelectedIndex = 0 };
        using var prompt = new AIPromptPanel("Practice.md", f.Window.AIWorkspace, (_, _, _) => Task.FromResult("unused"), () => { }, () => Task.CompletedTask, _ => { }, scope: scope);
        var window = new Window { Width = 1000, Height = 900, Content = prompt }; window.Show(f.Window); Pump(window);
        try
        {
            Assert.DoesNotContain(prompt.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Create content"));
            scope.SelectedItem = "Entire note"; Pump(window);
            Assert.DoesNotContain(prompt.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Create content"));
            scope.SelectedItem = "Instruction only"; Pump(window);
            Assert.DoesNotContain(prompt.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Create content"));
            scope.SelectedIndex = 0; Pump(window);
            Assert.DoesNotContain(prompt.GetVisualDescendants().OfType<Expander>(), e => Equals(e.Header, "Inspect exact text to share"));
            Assert.DoesNotContain(prompt.GetVisualDescendants(), c => AutomationProperties.GetName(c) == "Exact AI writing scope");
            Capture(window, selection ? "writing-selected" : "writing-entire-note");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Paper)]
    [InlineData(ThemeChoice.Midnight)]
    public async Task Welcome_is_centered_and_review_shows_complete_context_with_guidance(ThemeChoice theme)
    {
        using var f = new DesktopFixture(); SetTheme(f, theme, 1);
        f.Window.Width = 1440; f.Window.Height = 1000;
        await f.Window.RunCommandAsync(CommandIds.Welcome); Pump(f.Window);
        var welcome = Assert.Single(f.Window.OverlayLayer.Children.OfType<Border>());
        Assert.Equal(780, welcome.MaxWidth);
        var center = welcome.TranslatePoint(new Point(welcome.Bounds.Width / 2, welcome.Bounds.Height / 2), f.Window.OverlayLayer)!.Value;
        Assert.InRange(Math.Abs(center.X - f.Window.OverlayLayer.Bounds.Width / 2), 0, 2);
        Assert.InRange(Math.Abs(center.Y - f.Window.OverlayLayer.Bounds.Height / 2), 0, 2);
        Capture(f.Window, $"welcome-{theme}"); f.Window.HideOverlay();
        const string original = "# Original\n\nFirst section\n\nUnchanged context\n\nLast section";
        const string proposed = "# Improved\n\nClearer first section\n\nUnchanged context\n\nClearer last section";
        var applied = false;
        var review = new AIEditReview(original, proposed, 0, _ => { applied = true; return true; }, () => { }, _ => { }, () => { });
        var window = new Window { Width = 1100, Height = 850, Content = review }; window.Show(f.Window); Pump(window);
        try
        {
            var boxes = review.GetVisualDescendants().OfType<TextBox>().ToArray();
            Assert.Equal(2, boxes.Length); Assert.Equal(original, boxes[0].Text); Assert.Equal(proposed, boxes[1].Text);
            Assert.True(boxes[0].Bounds.Height >= 200, $"Review reading height: {boxes[0].Bounds.Height}");
            Assert.False(applied);
            Assert.Contains(review.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("1. Compare your current text") == true);
            Capture(window, $"review-{theme}");
            window.Width = 600; Pump(window); Capture(window, $"review-compact-{theme}");
            Assert.True(boxes[1].TranslatePoint(default, window)!.Value.Y > boxes[0].TranslatePoint(default, window)!.Value.Y);
        }
        finally { window.Close(); }
    }

    private static void SetTheme(DesktopFixture f, ThemeChoice theme, double scale) { f.Shell.Settings.Theme = theme; f.Shell.Settings.UiScale = scale; f.Window.ApplyTheme(); f.Window.ApplySettings(); }
    private static void Pump(Window w) { for (var i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); } }
    private static void Capture(Window w, string name)
    {
        var directory = Path.Combine(Screenshots.Folder, "six-screen-fix"); Directory.CreateDirectory(directory);
        using var frame = w.CaptureRenderedFrame(); Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(directory, name + ".png")); frame.Save(stream);
    }
}
