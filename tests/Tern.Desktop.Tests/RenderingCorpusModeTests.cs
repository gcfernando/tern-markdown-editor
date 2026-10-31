using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tern.App.Commands;
using Tern.Rendering.Preview;

namespace Tern.Desktop.Tests;

public sealed class RenderingCorpusModeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    [AvaloniaFact]
    public async Task Every_original_corpus_path_preserves_source_and_preview_across_window_modes()
    {
        var root = FindRoot();
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "artifacts", "rendering-validation", "inventory.json")));
        var records = manifest.RootElement.GetProperty("files").EnumerateArray().Select(f => f.Clone()).ToArray();
        Assert.NotEmpty(records);
        var results = new List<object>();
        var failures = new List<string>();
        foreach (var source in manifest.RootElement.GetProperty("sources").EnumerateArray())
        {
            using var fixture = new DesktopFixture();
            var sourceId = source.GetProperty("id").GetString()!;
            Assert.True(await fixture.Shell.OpenWorkspaceAsync(source.GetProperty("root").GetString()!, recordRecent: false));
            foreach (var file in records.Where(f => f.GetProperty("source").GetString() == sourceId))
            {
                var id = file.GetProperty("id").GetString()!;
                var path = file.GetProperty("localPath").GetString()!;
                var hash = file.GetProperty("sha256").GetString()!;
                var modes = new List<object>();
                string? failure = null;
                try
                {
                    Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
                    var tab = await fixture.Shell.OpenFileAsync(path, recordRecent: false);
                    Assert.NotNull(tab);
                    var text = tab.CurrentText;
                    string[]? anchors = null;
                    string[]? outline = null;
                    foreach (var mode in new[] { CommandIds.ViewSource, CommandIds.ViewSplit, CommandIds.ViewReading, CommandIds.ViewSource })
                    {
                        await fixture.Window.RunCommandAsync(mode);
                        await fixture.Window.RebuildPreviewAsync();
                        fixture.Window.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();
                        Assert.Equal(mode != CommandIds.ViewReading, fixture.Window.EditorHost.IsVisible);
                        Assert.Equal(mode != CommandIds.ViewSource, fixture.Window.PreviewHost.IsVisible);
                        Assert.Equal(text, tab.CurrentText);
                        Assert.Equal(text, fixture.Window.EditorControl.Document.Text);
                        Assert.False(tab.IsDirty);
                        if (mode != CommandIds.ViewSource)
                        {
                            var preview = fixture.Window.PreviewControl.Document;
                            Assert.NotNull(preview);
                            Assert.Equal(text.Length, preview.SourceLength);
                            Assert.Equal(TruncationReason.None, preview.Truncation);
                            var currentAnchors = preview.Anchors.Keys.Order(StringComparer.Ordinal).ToArray();
                            var currentOutline = preview.Outline.Select(h => $"{h.Level}:{h.Line}:{h.Anchor}:{h.Text}").ToArray();
                            if (anchors is not null) Assert.Equal(anchors, currentAnchors);
                            if (outline is not null) Assert.Equal(outline, currentOutline);
                            anchors = currentAnchors;
                            outline = currentOutline;
                            Assert.All(preview.Outline, heading => Assert.NotNull(preview.FindAnchorSourceLine(heading.Anchor)));
                            modes.Add(new { mode, status = "Pass", scope = "Headless MainWindow pane/source/preview-length/anchor consistency", preview.SourceLength, preview.NodeCount, anchors = currentAnchors, outline = currentOutline });
                        }
                        else modes.Add(new { mode, status = "Pass", scope = "Headless MainWindow pane/source preservation" });
                    }
                    Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
                    Assert.True(await fixture.Shell.CloseTabAsync(tab));
                }
                catch (Exception exception)
                {
                    failure = exception.ToString();
                    failures.Add(id + ": " + exception.Message);
                    // Continue accounting for the corpus; fixture disposal never saves originals.
                    fixture.Dialogs.SaveChoice = Tern.App.Shell.SaveChoice.Discard;
                    foreach (var tab in fixture.Shell.Tabs.ToArray()) await fixture.Shell.CloseTabAsync(tab);
                }
                results.Add(new { fileId = id, localPath = path, sha256 = hash, status = failure is null ? "Pass" : "Fail", scope = "Headless mode integration only", modes, failure, networkAssets = "Blocked: network behavior not exercised", nativeVisual = "Blocked: physical display interaction not exercised" });
            }
        }
        var output = Path.Combine(root, "artifacts", "rendering-validation", "tern-modes.json");
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { schemaVersion = 1, testedAt = DateTimeOffset.UtcNow, discovered = records.Length, tested = results.Count, results }, JsonOptions));
        Assert.Equal(records.Length, results.Count);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Tern.slnx")) || File.Exists(Path.Combine(directory.FullName, "Tern.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Rendering corpus workspace root not found.");
    }
}
