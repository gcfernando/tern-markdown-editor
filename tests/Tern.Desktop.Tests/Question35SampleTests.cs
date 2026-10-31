using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.Domain.Text;

namespace Tern.Desktop.Tests;

/// <summary>Exercises the exact supplied normal-size file behind the question 35 debugger screenshots.</summary>
public sealed class Question35SampleTests
{
    [AvaloniaFact]
    public async Task Supplied_supervised_learning_file_switches_Source_Split_Reading_without_parser_cancellation_or_source_changes()
    {
        var supplied = SamplePath(); var originalBytes = await File.ReadAllBytesAsync(supplied); var originalHash = SHA256.HashData(originalBytes);
        var text = await File.ReadAllTextAsync(supplied);
        using var fixture = new DesktopFixture();
        var copy = await fixture.WriteAsync("supervised_learning.md", text);
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
        var path = Path.Combine(directory.FullName, "MD Files Samples 02", "supervised_learning.md");
        Assert.True(File.Exists(path), "The user-supplied question 35 fixture must be available; this test does not silently skip it.");
        return path;
    }
    private static async Task Settle(DesktopFixture fixture)
    {
        for (var pass = 0; pass < 6; pass++)
        { fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); await Task.Delay(40); }
    }
}
