using Tern.App.Documents;
using Tern.Domain.Text;
using Tern.Markdown;

namespace Tern.App.Tests;

public sealed class OutsideAttachmentSaveTests
{
    [Theory]
    [InlineData(OutsideAttachmentChoice.SaveWithoutCopying)]
    [InlineData(OutsideAttachmentChoice.RewriteAbsolute)]
    [InlineData(OutsideAttachmentChoice.Cancel)]
    public async Task Save_as_reviews_all_outside_attachments_and_preserves_originals(OutsideAttachmentChoice choice)
    {
        using var f = new ShellFixture();
        var image = await f.WriteAsync(@"images\a space.png", "original image");
        var attachment = await f.WriteAsync(@"files\readme.txt", "original attachment");
        const string text = "![image](../images/a%20space.png)\n[readme](../files/readme.txt#part)";
        var source = await f.WriteAsync(@"notes\note.md", text);
        var tab = (await f.Shell.OpenFileAsync(source))!;
        f.Dialogs.SavePath = f.Root.File(@"copies\note.md");
        f.Dialogs.OutsideAttachmentChoice = choice;
        var saved = await f.Shell.SaveTabAsAsync(tab);
        Assert.Equal(choice != OutsideAttachmentChoice.Cancel, saved);
        Assert.Equal(2, Assert.Single(f.Dialogs.OutsideAttachmentPrompts).Count);
        Assert.Equal(text, await File.ReadAllTextAsync(source));
        Assert.Equal("original image", await File.ReadAllTextAsync(image));
        Assert.Equal("original attachment", await File.ReadAllTextAsync(attachment));
        Assert.False(File.Exists(f.Root.File(@"images\a space.png")));
        if (choice == OutsideAttachmentChoice.Cancel)
        {
            Assert.False(File.Exists(f.Dialogs.SavePath));
            Assert.Equal(source, tab.FilePath);
            Assert.Equal(text, tab.CurrentText);
            return;
        }
        var savedText = await File.ReadAllTextAsync(f.Dialogs.SavePath);
        Assert.Equal(savedText, tab.CurrentText);
        Assert.False(tab.IsDirty);
        if (choice == OutsideAttachmentChoice.SaveWithoutCopying)
        {
            Assert.Equal(text, savedText);
            Assert.Contains("../images/a space.png", f.Shell.StatusMessage);
            Assert.Contains("../files/readme.txt", f.Shell.StatusMessage);
            Assert.Contains("may break", f.Shell.StatusMessage);
        }
        else
        {
            Assert.Contains(LinkTarget.FormatPath(image), savedText);
            Assert.Contains(LinkTarget.FormatPath(attachment, "part"), savedText);
        }
    }

    [Fact]
    public async Task Copied_markdown_dependencies_receive_the_same_outside_link_choice()
    {
        using var f = new ShellFixture();
        var image = await f.WriteAsync(@"images\picture.png", "kept bytes");
        const string child = "![picture](../../images/picture.png)";
        await f.WriteAsync(@"notes\child\linked.md", child);
        var source = await f.WriteAsync(@"notes\note.md", "[child](child/linked.md)");
        var tab = (await f.Shell.OpenFileAsync(source))!;
        f.Dialogs.SavePath = f.Root.File(@"copies\note.md");
        f.Dialogs.OutsideAttachmentChoice = OutsideAttachmentChoice.RewriteAbsolute;
        Assert.True(await f.Shell.SaveTabAsAsync(tab));
        Assert.Equal(child, await File.ReadAllTextAsync(Path.Combine(f.Docs, @"notes\child\linked.md")));
        Assert.Contains(LinkTarget.FormatPath(image), await File.ReadAllTextAsync(f.Root.File(@"copies\child\linked.md")));
        Assert.Single(Assert.Single(f.Dialogs.OutsideAttachmentPrompts));
    }

    [Fact]
    public async Task Changed_dependency_cannot_silently_add_an_unreviewed_outside_attachment()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("first.png", "one");
        await f.WriteAsync("second.png", "two");
        var child = await f.WriteAsync(@"notes\child.md", "![first](../first.png)");
        var source = await f.WriteAsync(@"notes\note.md", "[child](child.md)");
        var tab = (await f.Shell.OpenFileAsync(source))!;
        f.Dialogs.SavePath = f.Root.File(@"copies\note.md");
        f.Dialogs.OutsideAttachmentChoice = OutsideAttachmentChoice.SaveWithoutCopying;
        f.Dialogs.OnOutsideAttachmentPrompt = () => File.WriteAllText(child, "![second](../second.png)");
        Assert.False(await f.Shell.SaveTabAsAsync(tab));
        Assert.False(File.Exists(f.Dialogs.SavePath));
        Assert.False(File.Exists(f.Root.File(@"copies\child.md")));
        Assert.Equal(source, tab.FilePath);
    }

    [Fact]
    public async Task Conflict_copy_honors_the_choice_without_changing_the_open_documents_identity_or_dirty_text()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("image.png", "kept");
        var source = await f.WriteAsync(@"notes\note.md", "![image](../image.png)");
        var tab = (await f.Shell.OpenFileAsync(source))!;
        tab.ApplyEdits([TextEdit.Insert(0, "my edits\n")]);
        var text = tab.CurrentText;
        await File.WriteAllTextAsync(source, "external disk contents");
        await tab.CheckDiskAsync(false);
        f.Dialogs.SavePath = f.Root.File(@"copies\note.md");
        f.Dialogs.ConflictChoice = Tern.App.Shell.ConflictChoice.SaveCopy;
        f.Dialogs.OutsideAttachmentChoice = OutsideAttachmentChoice.SaveWithoutCopying;
        await f.Shell.ResolveConflictAsync(tab);
        Assert.Single(f.Dialogs.OutsideAttachmentPrompts);
        Assert.Equal(text, await File.ReadAllTextAsync(f.Dialogs.SavePath));
        Assert.Equal(text, tab.CurrentText);
        Assert.True(tab.IsDirty);
        Assert.Equal(source, tab.FilePath);
        Assert.Equal("external disk contents", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task Editing_during_the_choice_does_not_save_unreviewed_new_text()
    {
        using var f = new ShellFixture();
        await f.WriteAsync("image.png", "kept");
        var source = await f.WriteAsync(@"notes\note.md", "![image](../image.png)");
        var tab = (await f.Shell.OpenFileAsync(source))!;
        f.Dialogs.SavePath = f.Root.File(@"copies\note.md");
        f.Dialogs.OutsideAttachmentChoice = OutsideAttachmentChoice.SaveWithoutCopying;
        f.Dialogs.OnOutsideAttachmentPrompt = () => tab.ApplyEdits([TextEdit.Insert(0, "new edit\n")]);
        Assert.False(await f.Shell.SaveTabAsAsync(tab));
        Assert.False(File.Exists(f.Dialogs.SavePath));
        Assert.StartsWith("new edit", tab.CurrentText);
        Assert.Equal(source, tab.FilePath);
    }
}
