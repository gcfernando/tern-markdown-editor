using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.Desktop.Services;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class ScreenReviewRegressionTests
{
    [AvaloniaFact]
    public void Prompt_without_a_safe_default_focuses_cancel_and_not_the_destructive_button()
    {
        using var fixture = new DesktopFixture();
        // The destructive button is requested as default, so the prompt must drop that default.
        var prompt = new PromptWindow("Delete?", "This cannot be undone.", ["Delete", "Keep"], 0, 1, destructiveIndex: 0);
        prompt.Show(fixture.Window);
        Pump(prompt);
        var buttons = prompt.GetVisualDescendants().OfType<Button>().ToArray();
        Assert.False(Assert.Single(buttons, button => Equals(button.Content, "Delete")).IsFocused);
        Assert.True(Assert.Single(buttons, button => Equals(button.Content, "Keep")).IsFocused);
        prompt.Close();
    }

    [AvaloniaFact]
    public void Move_double_click_counts_only_on_a_folder_row()
    {
        var row = new ListBoxItem { Content = new TextBlock { Text = "Archive" } };
        var list = new ListBox { Items = { row } };
        var window = new Window { Content = list };
        window.Show();
        Pump(window);
        Assert.True(MoveToWindow.IsOnItem(row));
        Assert.True(MoveToWindow.IsOnItem(row.GetVisualDescendants().OfType<TextBlock>().First()));
        Assert.False(MoveToWindow.IsOnItem(list));
        Assert.False(MoveToWindow.IsOnItem(null));
        window.Close();
    }

    [Fact]
    public async Task Damaged_cached_address_falls_back_to_a_fresh_download()
    {
        var root = Path.Combine(Path.GetTempPath(), "tern-url-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string url = "https://example.test/note.md";
            var directory = Path.Combine(root, MarkdownUrlLoader.Hash(MarkdownUrlLoader.GetFetchAddress(url).AbsoluteUri));
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), """{"Source":"not a valid address","Name":"note.md","Assets":[],"Warnings":[]}""");
            await File.WriteAllTextAsync(Path.Combine(directory, "document.md"), "# Synthetic");
            await File.WriteAllTextAsync(Path.Combine(directory, "original.md"), "# Synthetic");
            Assert.Null(await MarkdownUrlCache.ReadAsync(root, url));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task Unexpected_preview_failure_is_shown_and_does_not_stop_later_previews()
    {
        using var fixture = new DesktopFixture();
        fixture.Shell.NewDocument("# Keep this Markdown");
        await fixture.Window.RunCommandAsync(CommandIds.ViewSplit);
        fixture.Window.BeforePreviewBuild = () => throw new FormatException("Synthetic preview failure");
        await fixture.Window.RebuildPreviewAsync();
        Pump(fixture.Window);
        Assert.True(fixture.Window.PreviewProblem.IsVisible);

        fixture.Window.BeforePreviewBuild = null;
        await fixture.Window.RebuildPreviewAsync();
        Pump(fixture.Window);
        Assert.NotNull(fixture.Window.PreviewControl.Document);
        Assert.False(fixture.Window.PreviewProblem.IsVisible);
    }

    [Fact]
    public void Plain_keeps_readable_paste_and_picker_reasons_but_hides_system_details()
    {
        const string paste = "The HTML clipboard has invalid fragment offsets. Use Paste Plain Text instead.";
        Assert.Equal(paste, Tern.App.Shell.UserProblem.Plain(new InvalidDataException(paste)));
        Assert.StartsWith("Windows could not complete", Tern.App.Shell.UserProblem.Plain(new System.Runtime.InteropServices.ExternalException("Native picker failure")));
        Assert.DoesNotContain("HRESULT", Tern.App.Shell.UserProblem.Plain(new System.Runtime.InteropServices.COMException("Error HRESULT E_FAIL has been returned")));
        Assert.DoesNotContain("secret", Tern.App.Shell.UserProblem.Plain(new System.Runtime.InteropServices.ExternalException(@"Could not read C:\secret\a.md")));
        Assert.DoesNotContain("secret", Tern.App.Shell.UserProblem.Plain(new System.ComponentModel.Win32Exception(5, "secret failure")));
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 5; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    }
}
