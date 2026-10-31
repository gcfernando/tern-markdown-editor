using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Views;
using Tern.Domain.Text;

namespace Tern.Desktop.Tests;

public sealed class IntegrationFollowupTests
{
    [AvaloniaFact]
    public async Task Reordering_tabs_retains_editor_buffer_and_undo_history()
    {
        using var fixture = new DesktopFixture();
        var a = (await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("a.md", "a")))!;
        await fixture.Shell.OpenFileAsync(await fixture.WriteAsync("b.md", "b"));
        fixture.Shell.ActiveTab = a;
        a.ApplyEdits([TextEdit.Insert(1, " edited")]);
        var document = fixture.Window.EditorControl.Document;
        fixture.Shell.MoveTab(0, 1);
        Assert.Same(document, fixture.Window.EditorControl.Document);
        await fixture.Window.RunCommandAsync(CommandIds.Undo);
        Assert.Equal("a", a.CurrentText);
    }

    [AvaloniaFact]
    public async Task Changing_save_encoding_of_a_clean_file_preserves_text_and_does_not_redecode_bytes()
    {
        using var fixture = new DesktopFixture();
        var path = await fixture.WriteAsync("unicode.md", "café 😀\n");
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        await fixture.Window.RunCommandAsync(CommandIds.ChangeEncoding);
        var rows = fixture.Window.Picker!.Results.Items.OfType<PickerItem>().ToList();
        fixture.Window.Picker.Results.SelectedIndex = rows.FindIndex(r => r.Payload is Tern.Filesystem.EncodingOption { Kind: TextEncodingKind.Utf16LittleEndian, HasBom: false });
        fixture.Window.Picker.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("café 😀\n", tab.CurrentText);
        Assert.True(tab.IsDirty);
        Assert.Equal(TextEncodingKind.Utf16LittleEndian, tab.Session.Format.Encoding);
        Assert.Equal("café 😀\n", await File.ReadAllTextAsync(path));
    }

    [AvaloniaFact]
    public async Task Closing_a_document_while_link_repair_is_awaiting_analysis_does_not_edit_a_disposed_tab()
    {
        using var fixture = new DesktopFixture();
        var source = await fixture.WriteAsync("source.md", "[broken](missing.md)\n");
        var target = await fixture.WriteAsync("target.md", "# Target\n");
        await fixture.Shell.OpenWorkspaceAsync(fixture.Docs);
        var tab = (await fixture.Shell.OpenFileAsync(source))!;
        var repair = fixture.Window.RepairLinkAsync(tab, new LinkRowView("broken", "missing.md", 1, true, "✕"), target);
        Assert.True(await fixture.Shell.CloseTabAsync(tab));
        await repair;
        Assert.Equal("[broken](missing.md)\n", await File.ReadAllTextAsync(source));
    }
}
