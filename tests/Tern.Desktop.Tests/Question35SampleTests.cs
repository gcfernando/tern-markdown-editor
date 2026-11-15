using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.Domain.Text;

namespace Tern.Desktop.Tests;

/// <summary>Exercises the currently supplied Markdown, falling back to the original question 35 fixture.</summary>
public sealed class Question35SampleTests
{
    [AvaloniaFact]
    public async Task Supplied_workbook_search_keeps_all_results_and_current_match_across_modes()
    {
        var supplied = SamplePath();
        var bytes = await File.ReadAllBytesAsync(supplied);
        var text = await File.ReadAllTextAsync(supplied);
        using var fixture = new DesktopFixture();
        var copy = await fixture.WriteAsync("search-workbook.md", text);
        var tab = (await fixture.Shell.OpenFileAsync(copy))!;
        var revision = tab.Session.Revision;
        var expected = 0;
        for (var offset = 0; (offset = text.IndexOf("regression", offset, StringComparison.OrdinalIgnoreCase)) >= 0; offset += 10)
            expected++;
        Assert.True(expected > 1);
        await fixture.Window.RunCommandAsync(CommandIds.ViewSource);
        fixture.Window.EditorControl.Select(0, 0);
        fixture.Window.ShowFindBar(false);
        await fixture.Window.SearchAsync("regression");
        foreach (var mode in new[] { CommandIds.ViewSource, CommandIds.ViewSplit, CommandIds.ViewReading })
        {
            await fixture.Window.RunCommandAsync(mode);
            await fixture.Window.RebuildPreviewAsync();
            await Settle(fixture);
            Assert.Equal(expected, fixture.Window.FindResults.ItemCount);
            var renderer = Assert.Single(fixture.Window.EditorControl.TextArea.TextView.BackgroundRenderers.OfType<Tern.Desktop.Views.FindMatchRenderer>());
            Assert.Equal(expected, renderer.Matches.Count);
            var before = fixture.Window.EditorControl.SelectionStart;
            fixture.Window.FindNext(true);
            await Settle(fixture);
            Assert.NotEqual(before, fixture.Window.EditorControl.SelectionStart);
            Assert.Equal("regression", fixture.Window.EditorControl.SelectedText, ignoreCase: true);
            Assert.True(fixture.Window.FindBox.IsKeyboardFocusWithin);
        }
        Assert.Equal(text, tab.CurrentText);
        Assert.Equal(revision, tab.Session.Revision);
        Assert.False(tab.IsDirty);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(supplied));
        Assert.Equal(text, await File.ReadAllTextAsync(copy));
    }

    [AvaloniaFact]
    public async Task Supplied_document_switches_Source_Split_Reading_without_parser_cancellation_or_source_changes()
    {
        var supplied = SamplePath(); var originalBytes = await File.ReadAllBytesAsync(supplied); var originalHash = SHA256.HashData(originalBytes);
        var text = await File.ReadAllTextAsync(supplied);
        using var fixture = new DesktopFixture();
        var copy = await fixture.WriteAsync(Path.GetFileName(supplied), text);
        var observed = new ConcurrentQueue<string>();
        void Observe(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is OperationCanceledException)
            {
                var stack = e.Exception.StackTrace ?? Environment.StackTrace;
                if (stack.Contains("Tern.Rendering.Preview.PreviewBuilder", StringComparison.Ordinal)) observed.Enqueue(stack);
            }
        }
        AppDomain.CurrentDomain.FirstChanceException += Observe;
        try
        {
            var tab = (await fixture.Shell.OpenFileAsync(copy))!;
            await fixture.Window.RebuildPreviewAsync(); await Settle(fixture);
            var caret = Math.Min(text.Length / 2, fixture.Window.EditorControl.Document.TextLength);
            fixture.Window.EditorControl.CaretOffset = caret;
            var revision = tab.Session.Revision;
            for (var cycle = 0; cycle < 8; cycle++)
            {
                await fixture.Window.RunCommandAsync(CommandIds.ViewSource);
                await fixture.Window.RunCommandAsync(CommandIds.ViewSplit);
                await fixture.Window.RunCommandAsync(CommandIds.ViewReading);
            }
            await fixture.Window.RebuildPreviewAsync(); await Settle(fixture);
            var preview = fixture.Window.PreviewControl.Document;
            Assert.NotNull(preview); Assert.False(preview.Truncated);
            Assert.Equal(tab.CurrentText.Length, preview.SourceLength);
            Assert.NotEmpty(preview.Blocks); Assert.NotEmpty(preview.Outline);
            Assert.Equal(caret, fixture.Window.EditorControl.CaretOffset);
            Assert.Equal(text, tab.CurrentText); Assert.Equal(revision, tab.Session.Revision); Assert.False(tab.IsDirty);
            Assert.Equal(text, await File.ReadAllTextAsync(copy));
            Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(supplied)));
            Assert.DoesNotContain("Preview could not be updated", fixture.Shell.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cancel", fixture.Shell.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(observed);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Observe; }
    }

    [AvaloniaFact]
    public async Task Superseded_sample_renders_cannot_replace_the_latest_edited_document()
    {
        var text = await File.ReadAllTextAsync(SamplePath());
        using var fixture = new DesktopFixture();
        var copy = await fixture.WriteAsync("supervised_learning.md", text);
        var sample = (await fixture.Shell.OpenFileAsync(copy))!;
        var pending = fixture.Window.RebuildPreviewAsync();
        await fixture.Window.RunCommandAsync(CommandIds.ViewSource);
        await fixture.Window.RunCommandAsync(CommandIds.ViewSplit);
        sample.ApplyEdits([TextEdit.Insert(sample.CurrentText.Length, "\n\n## Latest reviewed sample revision\n\nCurrent text.\n")]);
        var latest = fixture.Shell.NewDocument("# Final active document\n\nLatest generation.");
        for (var i = 0; i < 4; i++)
        { await fixture.Window.RunCommandAsync(CommandIds.ViewSource); await fixture.Window.RunCommandAsync(CommandIds.ViewReading); }
        await pending;
        await fixture.Window.RebuildPreviewAsync(); await Settle(fixture);
        Assert.Same(latest, fixture.Shell.ActiveTab);
        var preview = fixture.Window.PreviewControl.Document;
        Assert.NotNull(preview); Assert.False(preview.Truncated);
        Assert.Equal(latest.CurrentText.Length, preview.SourceLength);
        Assert.Contains(preview.Outline, heading => heading.Text == "Final active document");
        Assert.DoesNotContain(preview.Outline, heading => heading.Text == "Latest reviewed sample revision");
        Assert.True(sample.IsDirty); Assert.Equal(text, await File.ReadAllTextAsync(copy));
        Assert.DoesNotContain("Preview could not be updated", fixture.Shell.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static string SamplePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tern.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var current = Path.Combine(directory.FullName, "inputs", "Data-Science-and-Machine-Learning.md");
        var path = File.Exists(current) ? current : Path.Combine(directory.FullName, "MD Files Samples 02", "supervised_learning.md");
        Assert.True(File.Exists(path), "Supply inputs/Data-Science-and-Machine-Learning.md or the original question 35 fixture; this test does not silently skip it.");
        return path;
    }
    private static async Task Settle(DesktopFixture fixture)
    {
        for (var pass = 0; pass < 6; pass++)
        { fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); await Task.Delay(40); }
    }
}
