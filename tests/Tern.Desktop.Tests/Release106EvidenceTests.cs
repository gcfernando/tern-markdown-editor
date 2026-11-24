using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.App.Settings;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

/// <summary>Rendered 1.0.6 screens. Uses synthetic notes and a simulated AI service.</summary>
public sealed partial class AIWritingWorkflowTests
{
    private static string Evidence(string name)
    {
        var folder = Path.Combine(Screenshots.Folder, "release-106");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name + ".png");
    }

    private static void Save(TopLevel top, string name)
    {
        Dispatcher.UIThread.RunJobs();
        using var bitmap = top.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        using var stream = File.Create(Evidence(name));
        bitmap.Save(stream, new PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public async Task Processing_stays_visible_with_Windows_animations_off_and_counts_under_Reduce_motion()
    {
        var feed = new Tern.App.Shell.ActivityFeed();
        using var operation = feed.Begin("Generating");
        var loading = new LoadingState(feed, operation.Id);
        var window = new Window { Width = 400, Height = 200, Content = loading };
        try
        {
            Tern.Desktop.Services.MotionPolicy.Apply(false, false);
            window.Show();
            await Task.Delay(Tern.Desktop.Services.MotionPolicy.LoadingRevealDelay + TimeSpan.FromMilliseconds(80));
            Pump(window);
            var bar = loading.GetVisualDescendants().OfType<ProgressBar>().Single();
            Assert.True(bar.IsVisible);
            Assert.True(bar.IsIndeterminate);

            Tern.Desktop.Services.MotionPolicy.Apply(true, false);
            Assert.False(bar.IsVisible);
            var elapsed = loading.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "OperationElapsed");
            await Until(() => elapsed.IsVisible);
            Assert.StartsWith("Still working", elapsed.Text, StringComparison.Ordinal);
            operation.Complete();
            await Until(() => !loading.IsVisible);
        }
        finally { window.Close(); Tern.Desktop.Services.MotionPolicy.Apply(false); }
    }

    [Theory]
    [InlineData(402,"""{"error":{"message":"Insufficient Balance"}}""")]
    [InlineData(400, """{"error":{"message":"Insufficient balance on this account"}}""")]
    public void Exhausted_credit_gets_the_billing_message(int status, string body) =>
        Assert.Contains("credits", Tern.Desktop.Services.AIServiceException.From(status, body, "").Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("HTTP 402 Payment Required", true)]
    [InlineData("You have exceeded your premium request quota", true)]
    [InlineData("Insufficient permissions for this model", false)]
    [InlineData("connection reset", false)]
    public void Copilot_account_limits_are_recognized(string diagnostics, bool expected) =>
        Assert.Equal(expected, Tern.Desktop.Services.CopilotTextClient.IsAccountLimit(diagnostics));

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Midnight)]
    [InlineData(ThemeChoice.Paper)]
    public async Task Capture_release_106_ai_generation_and_review(ThemeChoice theme)
    {
        var release = new TaskCompletionSource();
        using var f = Fixture(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return Reply("A shorter paragraph that keeps the release decision and its owner.");
        });
        f.Shell.Settings.Theme = theme;
        f.Window.ApplyTheme();
        f.Window.Width = 1400; f.Window.Height = 1000;
        const string selected = "The release review covered the schedule, the open defects and the owner of each follow-up.";
        f.Shell.NewDocument("# Release notes\n\n" + selected + "\n\n## Next steps\n\nKeep this section.");
        f.Window.Editor.Select("# Release notes\n\n".Length, selected.Length);
        f.Window.ShowAIWritingPrompt("Rewrite");
        Pump(f.Window);
        var panel = Assert.Single(f.Window.OverlayLayer.Children.OfType<AIPromptPanel>());
        Find<TextBox>(panel, "AI writing question").Text = "Make this shorter";
        Save(f.Window, $"ai-prompt-{theme}");
        Click(Find<Button>(panel, "Generate AI suggestion"));
        // The bar is revealed after a short delay so instant responses do not flash.
        await Until(() => panel.GetVisualDescendants().OfType<LoadingState>()
            .SelectMany(state => state.GetVisualDescendants().OfType<ProgressBar>()).Any(bar => bar.IsEffectivelyVisible));
        Pump(f.Window);
        Save(f.Window, $"ai-generating-{theme}");
        release.SetResult();
        await WaitForReview(f);
        Pump(f.Window);
        Save(f.Window, $"ai-review-{theme}");
    }

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Graphite)]
    [InlineData(ThemeChoice.Harbor)]
    [InlineData(ThemeChoice.Linen)]
    [InlineData(ThemeChoice.Mist)]
    public async Task Capture_release_106_themes_and_reading_options(ThemeChoice theme)
    {
        using var f = Fixture((_, _) => Task.FromResult(new HttpResponseMessage()));
        f.Shell.Settings.Theme = theme;
        f.Window.ApplyTheme();
        f.Window.Width = 1400; f.Window.Height = 900;
        f.Shell.NewDocument("# Theme check\n\nBody text with **bold** and a [link](https://example.com).\n\n- First\n- Second\n\n```csharp\nvar x = 35;\n```\n");
        await f.Window.RunCommandAsync(CommandIds.ViewSplit);
        Pump(f.Window);
        Save(f.Window, $"theme-{theme}");
        var options = f.Window.GetVisualDescendants().OfType<Button>()
            .Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == "Reading options");
        var flyout = Assert.IsType<Flyout>(options.Flyout);
        flyout.ShowAt(options);
        Pump(f.Window);
        if (TopLevel.GetTopLevel((Control)flyout.Content!) is { } popup) Save(popup, $"reading-options-{theme}");
        flyout.Hide();
        var settings = new SettingsWindow(new AppSettings { Theme = theme }, f.Shell.Registry);
        settings.Show(f.Window);
        await Task.Delay(100);
        Save(settings, $"settings-{theme}");
        settings.Close();
    }
}
