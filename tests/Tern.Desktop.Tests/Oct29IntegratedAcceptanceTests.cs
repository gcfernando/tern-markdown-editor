using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.Desktop.ViewModels;
using Tern.Domain.Text;

namespace Tern.Desktop.Tests;

/// <summary>Independent integration checks for the supplied October 29 workflows.</summary>
public sealed class Oct29IntegratedAcceptanceTests
{
    private static readonly JsonSerializerOptions EvidenceJson = new() { WriteIndented = true };
    [AvaloniaFact]
    public async Task Named_roots_plain_folder_and_manual_revert_share_consistent_command_and_close_state()
    {
        using var f = new DesktopFixture();
        Assert.False(f.Window.CanRunCommand(CommandIds.FindInWorkspace));
        Assert.True(await f.Window.CreateWorkspaceAtAsync(f.Docs, "Company"));
        var company = f.Shell.Workspace!;
        Assert.True(await f.Window.CreateWorkspaceAtAsync(company.Root, "HR"));
        var hr = f.Shell.Workspace!;
        var path = Path.Combine(hr.Root, "people.md");
        const string saved = "# People\r\n\r\nExact saved text.  \r\n";
        await File.WriteAllTextAsync(path, saved, TestContext.Current.CancellationToken);
        await company.RescanTreeAsync();
        await hr.RescanTreeAsync();
        Assert.True(await f.Window.CreateWorkspaceAtAsync(f.Docs, "Diagrams"));
        await f.Window.ShowNavAsync("Files");
        Pump(f);
        var roots = f.Window.FileTree.Items.OfType<TreeRow>().ToArray();
        Assert.Equal(2, roots.Length);
        Assert.Contains(roots.SelectMany(r => r.Flatten()), r => r.FullPath == path);
        var tab = (await f.Shell.OpenFileAsync(path))!;
        tab.ApplyEdits([TextEdit.Insert(saved.Length, "changed")]);
        Assert.True(tab.IsDirty);
        tab.ApplyEdits([TextEdit.Delete(saved.Length, "changed".Length)]);
        Assert.False(tab.IsDirty);
        Assert.True(f.Window.CanRunCommand(CommandIds.Print));
        Assert.True(f.Window.CanRunCommand(CommandIds.ExportHtml));
        await f.Window.RebuildPreviewAsync();
        Capture(f, "workspace-hierarchy-and-clean-content.png");
        Assert.True(await f.Shell.CloseTabAsync(tab));
        Assert.Equal(0, f.Dialogs.SavePrompts);
        Assert.Equal(saved, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.True(await f.Shell.CloseAllWorkspacesAsync());
        Assert.False(f.Window.CanRunCommand(CommandIds.FindInWorkspace));
        Assert.True(await f.Shell.OpenFolderAsync(f.Docs));
        await f.Window.ShowNavAsync("Files");
        Pump(f);
        Assert.False(f.Shell.Workspace!.IsNamedWorkspace);
        Assert.False(f.Window.CanRunCommand(CommandIds.FindInWorkspace));
        Assert.False(File.Exists(Path.Combine(f.Docs, ".tern-workspace.json")));
        Assert.Contains(f.Window.FileTree.Items.OfType<TreeRow>().SelectMany(r => r.Flatten()), r => r.FullPath == path);
        Capture(f, "plain-folder-descendants.png");
    }

    [AvaloniaFact]
    public async Task Long_mixed_document_measures_render_and_page_navigation_without_changing_content()
    {
        using var f = new DesktopFixture();
        var text = new StringBuilder("# Long mixed acceptance document\n\n");
        for (var i = 0; i < 320; i++)
        {
            text.Append("## Section ").Append(i).Append("\n\n");
            for (var sentence = 0; sentence < 8; sentence++)
                text.Append("A paragraph with **emphasis**, ordinary wrapping and a [local link](note.md). ");
            text.Append("\n\n| Name | Value |\n| --- | --- |\n| Sample | 42 |\n\n");
            if (i % 20 == 0) text.Append("```mermaid\nflowchart LR\nA[Start] --> B[Finish]\n```\n\n![Sample](sample.png)\n\n");
        }
        await File.WriteAllBytesAsync(Path.Combine(f.Docs, "sample.png"),
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2WvYAAAAASUVORK5CYII="), TestContext.Current.CancellationToken);
        var source = text.ToString();
        var path = await f.WriteAsync("long-mixed.md", source);
        var tab = (await f.Shell.OpenFileAsync(path))!;
        var measurements = new List<object>();
        foreach (var mode in new[] { CommandIds.ViewSource, CommandIds.ViewSplit, CommandIds.ViewReading })
        {
            var timer = Stopwatch.StartNew();
            await f.Window.RunCommandAsync(mode);
            await f.Window.RebuildPreviewAsync();
            Pump(f);
            var renderMs = timer.Elapsed.TotalMilliseconds;
            if (mode != CommandIds.ViewSource)
            {
                f.Window.PreviewControl.ScrollToTop(); Pump(f);
                var before = f.Window.PreviewControl.TopSourceLine;
                timer.Restart();
                for (var page = 0; page < 12; page++) { f.Window.PreviewControl.ScrollByPage(1); Pump(f); }
                var after = f.Window.PreviewControl.TopSourceLine;
                Assert.True(after > before, $"{mode}: page navigation did not advance ({before} -> {after}).");
                measurements.Add(new { mode, renderMs, twelvePagesMs = timer.Elapsed.TotalMilliseconds, before, after });
                f.Window.PreviewControl.ScrollToTop(); Pump(f);
                Assert.Equal(1, f.Window.PreviewControl.TopSourceLine);
            }
            else measurements.Add(new { mode, renderMs });
            Assert.Equal(source, tab.CurrentText);
            Assert.False(tab.IsDirty);
            Capture(f, mode.Replace('.', '-') + "-mixed-document.png");
        }
        var output = OutputDirectory();
        await File.WriteAllTextAsync(Path.Combine(output, "mixed-document-navigation.json"), JsonSerializer.Serialize(new
        {
            characters = source.Length, sections = 320, tables = 320, diagrams = 16, images = 16,
            evidence = "Headless Avalonia layout and page navigation; not native touchpad, DPI or frame-time certification.", measurements,
        }, EvidenceJson), TestContext.Current.CancellationToken);
    }

    private static void Pump(DesktopFixture f)
    {
        for (var i = 0; i < 5; i++) { f.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    }

    private static string OutputDirectory()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Tern.slnx"))) root = root.Parent;
        var path = Path.Combine(root!.FullName, "docs", "review", "oct29", "after");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Capture(DesktopFixture f, string name)
    {
        Pump(f);
        using var frame = f.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var output = File.Create(Path.Combine(OutputDirectory(), name));
        frame.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
