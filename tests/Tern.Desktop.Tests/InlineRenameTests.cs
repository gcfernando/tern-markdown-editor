using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tern.App.Documents;
using Tern.Desktop.Views;

namespace Tern.Desktop.Tests;

public sealed class InlineRenameTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Focus_loss_commits_draft_or_file_names_and_Enter_followed_by_focus_loss_is_safe(bool saved, bool enter)
    {
        using var f = new DesktopFixture();
        var oldPath = saved ? await f.WriteAsync("original.md", "Preserve exact bytes") : null;
        var tab = saved ? (await f.Shell.OpenFileAsync(oldPath!))! : f.Shell.NewDocument("Unsaved text");
        var input = await Begin(f, tab);
        input.Text = "renamed.md";
        if (enter) f.Window.KeyPress(Key.Enter, RawInputModifiers.None, default, null);
        FocusElsewhere(f);
        await WaitUntil(() => tab.DisplayName == "renamed.md");
        Assert.False(input.IsVisible);
        Assert.Equal(saved ? "Preserve exact bytes" : "Unsaved text", tab.CurrentText);
        if (saved)
        {
            Assert.False(File.Exists(oldPath));
            Assert.Equal("Preserve exact bytes", await File.ReadAllTextAsync(tab.FilePath!));
        }
        else Assert.Null(tab.FilePath);
    }

    [AvaloniaTheory]
    [InlineData("CON.md")]
    [InlineData("occupied.md")]
    public async Task Invalid_focus_loss_keeps_typed_name_with_visible_error_and_allows_correction(string invalid)
    {
        using var f = new DesktopFixture();
        var original = await f.WriteAsync("original.md", "Original bytes");
        var occupied = await f.WriteAsync("occupied.md", "Existing destination");
        var tab = (await f.Shell.OpenFileAsync(original))!;
        var input = await Begin(f, tab);
        input.Text = invalid;
        FocusElsewhere(f);
        await Settle();
        Assert.True(input.IsVisible);
        Assert.Equal(invalid, input.Text);
        Assert.Contains("rename-error", input.Classes);
        var error = Assert.Single(((StackPanel)input.Parent!).Children.OfType<TextBlock>(), text => text.Name == "TabRenameError");
        Assert.True(error.IsVisible);
        Assert.False(string.IsNullOrWhiteSpace(error.Text));
        Assert.Equal(error.Text, f.Shell.StatusMessage);
        Assert.Equal("Original bytes", await File.ReadAllTextAsync(original));
        Assert.Equal("Existing destination", await File.ReadAllTextAsync(occupied));
        input.Focus(); input.Text = "corrected.md";
        f.Window.KeyPress(Key.Enter, RawInputModifiers.None, default, null);
        await WaitUntil(() => tab.DisplayName == "corrected.md");
        Assert.False(input.IsVisible);
        Assert.False(error.IsVisible);
    }

    [AvaloniaFact]
    public async Task Escape_cancels_the_edit_without_a_late_focus_loss_rename()
    {
        using var f = new DesktopFixture();
        var path = await f.WriteAsync("original.md", "Keep bytes");
        var tab = (await f.Shell.OpenFileAsync(path))!;
        var input = await Begin(f, tab);
        input.Text = "cancelled.md";
        f.Window.KeyPress(Key.Escape, RawInputModifiers.None, default, null);
        FocusElsewhere(f);
        await Settle();
        Assert.False(input.IsVisible);
        Assert.Equal(path, tab.FilePath);
        Assert.Equal("original.md", tab.DisplayName);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "cancelled.md")));
    }

    private static async Task<TextBox> Begin(DesktopFixture f, DocumentTab tab)
    {
        await Settle();
        var title = f.Window.TabStrip.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Name == "TabTitleText" && ReferenceEquals(text.DataContext, tab));
        var point = title.TranslatePoint(new Point(title.Bounds.Width / 2, title.Bounds.Height / 2), f.Window)!.Value;
        f.Window.MouseDown(point, MouseButton.Left); f.Window.MouseUp(point, MouseButton.Left);
        f.Window.MouseDown(point, MouseButton.Left); f.Window.MouseUp(point, MouseButton.Left);
        var input = Assert.Single(((StackPanel)title.Parent!).Children.OfType<TextBox>());
        Assert.True(input.IsVisible);
        Assert.True(input.IsFocused);
        return input;
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Settle();
        Assert.True(condition());
    }
    private static void FocusElsewhere(DesktopFixture f) => Assert.True(f.Window.FindControl<Button>("OpenDocumentsButton")!.Focus());
    private static async Task Settle() { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
}
