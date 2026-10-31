using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.Desktop.Services;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;
using Tern.Domain.Text;

namespace Tern.Desktop.Tests;

public sealed class OpenUrlIntegrationTests
{
    [AvaloniaFact]
    public async Task Imported_draft_does_not_read_relative_images_from_an_unrelated_open_workspace()
    {
        using var fixture = new DesktopFixture();
        await fixture.WriteAsync("local.md", "Local note");
        var imagePath = Path.Combine(fixture.Docs, "owned.png");
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9l8AAAAASUVORK5CYII="));
        Assert.True(await fixture.Shell.OpenWorkspaceAsync(fixture.Docs));
        await fixture.Window.OpenUrlDocumentAsync(new MarkdownUrlDocument(new Uri("https://example.invalid/note.md"), "note.md", "![Remote relative image](owned.png)"));
        await fixture.Window.RebuildPreviewAsync();
        var paragraph = Assert.IsType<ParagraphNode>(Assert.Single(fixture.Window.PreviewControl.Document!.Blocks));
        Assert.IsType<Blocked>(Assert.Single(paragraph.Inlines.OfType<ImageNode>()).Resolution);
        Assert.True(File.Exists(imagePath));
        Assert.True(fixture.Shell.ActiveTab!.IsExternalDraft);
    }

    [AvaloniaFact]
    public async Task Online_text_opens_as_a_named_clean_source_without_mutating_local_files()
    {
        using var fixture = new DesktopFixture();
        var path = await fixture.WriteAsync("guide.md", "Keep local file");
        var existing = await fixture.Shell.OpenFileAsync(path);
        const string text = "# Remote guide\r\n\r\n**Keep source exactly.**\r\n";
        await fixture.Window.OpenUrlDocumentAsync(new MarkdownUrlDocument(new Uri("https://example.invalid/guide.md"), "guide.md", text));
        Dispatcher.UIThread.RunJobs();
        var copy = fixture.Shell.ActiveTab!;
        Assert.NotSame(existing, copy);
        Assert.Null(copy.FilePath);
        Assert.Equal("guide.md", copy.DisplayName);
        Assert.Equal(text, copy.CurrentText);
        Assert.False(copy.IsDirty);
        Assert.Equal("https://example.invalid/guide.md", copy.SourceUrl);
        Assert.Equal(LineEnding.CrLf, copy.Session.Format.DominantLineEnding);
        Assert.Equal("Keep local file", await File.ReadAllTextAsync(path));
        Assert.Single(fixture.Shell.Recent.Files);
        Assert.True(fixture.Window.CanRunCommand(CommandIds.SaveAs));
        Assert.Contains("Online file opened", fixture.Shell.StatusMessage, StringComparison.Ordinal);
        var fileMenu = fixture.Window.FindControl<Menu>("ApplicationMenu")!.Items.OfType<MenuItem>().First();
        Assert.Contains(fileMenu.Items.OfType<MenuItem>(), item => Equals(item.Tag, CommandIds.OpenUrl));
    }
}
