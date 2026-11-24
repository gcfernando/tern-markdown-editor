using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tern.App.Commands;
using Tern.Desktop.Services;
using Tern.Desktop.ViewModels;
using Tern.Desktop.Views;
using Tern.Domain.Text;

namespace Tern.Desktop.Tests;

public sealed class SafetyCleanupTests
{
    [AvaloniaTheory]
    [InlineData(ExportFormat.Html, "button")]
    [InlineData(ExportFormat.Pdf, "button")]
    [InlineData(ExportFormat.Text, "button")]
    [InlineData(ExportFormat.Html, "window")]
    [InlineData(ExportFormat.Pdf, "window")]
    [InlineData(ExportFormat.Text, "window")]
    [InlineData(ExportFormat.Html, "escape")]
    [InlineData(ExportFormat.Pdf, "escape")]
    [InlineData(ExportFormat.Text, "escape")]
    public async Task Cancel_stops_export_and_restores_retry_without_touching_existing_output(ExportFormat format, string route)
    {
        using var f = new DesktopFixture();
        var extension = format switch { ExportFormat.Html => "html", ExportFormat.Pdf => "pdf", _ => "txt" };
        var path = Path.Combine(f.Docs, "copy." + extension);
        await File.WriteAllTextAsync(path, "keep existing output");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new ExportWindow("draft.md", null, "# Draft", Tern.Markdown.MarkdownProfile.Extended, null, format)
        { BeforeExport = () => gate.Task };
        window.Show(f.Window);
        try
        {
            var pending = window.ExportAsync(path);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var cancel = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Cancel"));
            Assert.True(cancel.IsEffectivelyEnabled);
            if (route == "window") window.Close();
            else if (route == "escape") window.KeyPress(Key.Escape, RawInputModifiers.None, default, null);
            else cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(window.IsExporting);
            Assert.True(window.IsVisible);
            Assert.Contains("canceled", window.StatusText);
            Assert.Equal("keep existing output", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.GetFiles(f.Docs));
            window.BeforeExport = null;
            Assert.True(await window.ExportAsync(path));
        }
        finally { gate.TrySetResult(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task Repeated_window_close_reports_the_pending_save_prompt_without_losing_the_draft()
    {
        using var f = new DesktopFixture();
        var tab = f.Shell.NewDocument("unsaved draft");
        var decision = new TaskCompletionSource<Tern.App.Shell.SaveChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Dialogs.SaveResponse = () => decision.Task;
        try
        {
            f.Window.Close();
            Assert.Equal(1, f.Dialogs.SavePrompts);
            f.Window.Close();
            Assert.Equal("Finish the open save prompt first.", f.Shell.StatusMessage);
            Assert.Equal(1, f.Dialogs.SavePrompts);
            Assert.True(f.Window.IsVisible);
            Assert.Contains(tab, f.Shell.Tabs);
        }
        finally
        {
            decision.TrySetResult(Tern.App.Shell.SaveChoice.Cancel);
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(f.Window.IsVisible);
        Assert.Equal("unsaved draft", tab.CurrentText);
        Assert.True(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task Encoding_reopen_suggests_utf16_and_title_uses_current_assembly_version()
    {
        using var f = new DesktopFixture();
        var path = Path.Combine(f.Docs, "utf16.md");
        var bytes = Encoding.Unicode.GetBytes("# Unicode\nText");
        await File.WriteAllBytesAsync(path, bytes);
        var tab = (await f.Shell.OpenFileAsync(path))!;
        Dispatcher.UIThread.RunJobs();
        f.Window.UpdateLayout();
        Assert.True(tab.IsReadOnly);
        Assert.Contains("Reopen with encoding", f.Shell.StatusMessage);
        await f.Window.RunCommandAsync(CommandIds.ReopenWithEncoding);
        Dispatcher.UIThread.RunJobs();
        f.Window.UpdateLayout();
        var suggested = Assert.IsType<PickerItem>(f.Window.Picker!.Results.Items[0]);
        var encoding = Assert.IsType<Tern.Filesystem.EncodingOption>(suggested.Payload);
        Assert.Equal(TextEncodingKind.Utf16LittleEndian, encoding.Kind);
        Assert.False(encoding.HasBom);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal("v" + typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            f.Window.VersionLabel.Text);
    }

    [Fact]
    public void Unregister_removes_only_registered_values_and_empty_owned_keys()
    {
        var entries = WindowsFileAssociations.Registration(@"C:\Synthetic\Tern.exe").ToDictionary();
        var original = entries.Keys.ToHashSet();
        string[] protectedEntries =
        [
            @"Software\Classes\.md|",
            @"Software\Classes\.md\OpenWithProgids|Other.Markdown",
            @"Software\RegisteredApplications|OtherEditor",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md\UserChoice|ProgId",
            @"Software\Classes\Applications\Tern.exe|ForeignValue",
        ];
        foreach (var key in protectedEntries) entries[key] = "preserve";
        var candidates = new List<string>();
        var removed = new List<string>();
        WindowsFileAssociations.RemoveRegistration(@"C:\Synthetic\Tern.exe", (key, name) =>
        {
            var entry = key + "|" + name;
            Assert.True(original.Contains(entry));
            Assert.True(entries.Remove(entry));
            removed.Add(entry);
        }, key => candidates.Add(key));

        Assert.Equal(original.Count, removed.Count);
        Assert.Equal(protectedEntries.Order(), entries.Keys.Order());
        Assert.DoesNotContain(candidates, key => key.Contains("UserChoice") || key == @"Software\RegisteredApplications"
            || key == @"Software\Classes\.md\OpenWithProgids" || key == @"Software\Classes\.md");
        Assert.Contains(@"Software\Classes\Tern.Markdown\shell\open\command", candidates);
        Assert.Contains(@"Software\Classes\Tern.Markdown\shell\open", candidates);
        Assert.Contains(@"Software\Classes\Tern.Markdown", candidates);
        Assert.True(candidates.IndexOf(@"Software\Classes\Tern.Markdown\shell\open\command")
            < candidates.IndexOf(@"Software\Classes\Tern.Markdown"));
    }
}
