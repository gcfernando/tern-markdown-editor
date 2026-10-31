using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tern.Desktop.Views;
using Tern.Domain.Text;
using Tern.Filesystem;

namespace Tern.Desktop.Tests;

public sealed class AttributePickerTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unicode_picker_distinguishes_bom_variants_and_saves_exact_selected_bytes(bool hasBom)
    {
        using var fixture = new DesktopFixture();
        const string text = "Café\nsecond\n";
        var path = await fixture.WriteAsync("attributes.md", text);
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        await Settle();
        Assert.Equal("LF", fixture.Window.StatusLineEnding.Content);
        fixture.Window.StatusEncoding.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        fixture.Window.Picker!.Input.Text = "CP 1200 ·";
        await Settle();
        var rows = fixture.Window.Picker.Results.Items.OfType<PickerItem>().ToList();
        Assert.Equal(2, rows.Count);
        var index = rows.FindIndex(row => row.Payload is EncodingOption option && option.CodePage == 1200 && option.HasBom == hasBom);
        Assert.InRange(index, 0, rows.Count - 1);
        fixture.Window.Picker.Results.SelectedIndex = index;
        Accept(fixture.Window.Picker);
        await Settle();
        Assert.Equal(TextEncodingKind.Utf16LittleEndian, tab.Session.Format.Encoding);
        Assert.Equal(hasBom, tab.Session.Format.HasBom);
        Assert.Equal(text, tab.CurrentText);
        Assert.Equal(LineEnding.Lf, tab.Session.Format.DominantLineEnding);
        Assert.Contains(hasBom ? " BOM" : "(no BOM)", tab.EncodingText, StringComparison.Ordinal);
        Assert.True(tab.IsDirty);
        Assert.IsType<SaveSucceeded>(await tab.SaveAsync());
        Assert.Equal(TextFileCodec.Encode(text, tab.Session.Format), await File.ReadAllBytesAsync(path));
        var decoded = TextFileCodec.Decode(await File.ReadAllBytesAsync(path), TextEncodingKind.Utf16LittleEndian);
        Assert.True(decoded.IsLossless);
        Assert.Equal(hasBom, decoded.Format.HasBom);
        Assert.Equal(text, decoded.Text);
    }

    [AvaloniaFact]
    public async Task Searchable_codepage_picker_converts_japanese_without_changing_its_line_endings()
    {
        using var fixture = new DesktopFixture();
        const string text = "日本語\n";
        var path = await fixture.WriteAsync("japanese.md", text);
        var tab = (await fixture.Shell.OpenFileAsync(path))!;
        await Settle();
        fixture.Window.StatusEncoding.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        fixture.Window.Picker!.Input.Text = "CP 932";
        await Settle();
        var row = Assert.Single(fixture.Window.Picker.Results.Items.OfType<PickerItem>());
        var option = Assert.IsType<EncodingOption>(row.Payload);
        Assert.Equal(932, option.CodePage);
        Assert.Contains("shift_jis", row.Title, StringComparison.OrdinalIgnoreCase);
        Accept(fixture.Window.Picker);
        await Settle();
        Assert.Equal(option.Kind, tab.Session.Format.Encoding);
        Assert.False(tab.Session.Format.HasBom);
        Assert.Equal(text, tab.CurrentText);
        Assert.IsType<SaveSucceeded>(await tab.SaveAsync());
        Assert.Equal(text, TextFileCodec.Decode(await File.ReadAllBytesAsync(path), option.Kind).Text);
        Assert.Equal(LineEnding.Lf, tab.Session.Format.DominantLineEnding);
    }

    private static void Accept(QuickPicker picker) => picker.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

    private static async Task Settle()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(25);
        Dispatcher.UIThread.RunJobs();
    }
}
