using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.PreviewView.Tests;

public sealed class CorpusLocalAssetTests
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };
    [AvaloniaFact]
    public async Task Explicit_remote_acceptance_payloads_render_offline_in_native_controls()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Tern.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var directory = Path.Combine(root.FullName, "artifacts", "rendering-validation", "tern", "remote-acceptance");
        var reportPath = Path.Combine(directory, "results.json");
        if (!File.Exists(reportPath)) Assert.Skip("Run the explicit public-image acceptance probe first; this test performs no network requests.");
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        var frames = Path.Combine(directory, "frames"); Directory.CreateDirectory(frames);
        var results = new List<object>(); var failures = new List<string>();
        foreach (var entry in report.RootElement.GetProperty("urls").EnumerateArray())
        {
            var destination = entry.GetProperty("destination").GetString()!;
            var result = entry.GetProperty("result");
            if (result.GetProperty("Status").GetString() != "Pass") continue;
            var bytes = await File.ReadAllBytesAsync(result.GetProperty("BytePath").GetString()!, TestContext.Current.CancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)); Assert.Equal(result.GetProperty("Sha256").GetString(), hash);
            var first = System.Text.Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 200))).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            var mime = first.StartsWith('<') ? "image/svg+xml" : "image/png";
            using var image = new ImageView(new InlineImage(mime, bytes), destination, null);
            var window = new Window { Width = 800, Height = 600, Content = image };
            try
            {
                window.Show(); Harness.Pump(window);
                await image.LoadingCompletion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); Harness.Pump(window);
                var surface = image.Child;
                var loaded = image.State == ImageState.Loaded && surface is { Bounds.Width: > 0, Bounds.Height: > 0 }
                    && (surface is Image { Source: not null } || surface.GetType().Name == "SvgControl");
                using var capture = Harness.Capture(window); loaded &= capture is not null;
                var framePath = Path.Combine(frames, hash + ".png"); capture?.Save(framePath, new PngBitmapEncoderOptions());
                if (!loaded) failures.Add(destination + " => " + image.State);
                results.Add(new { destination, sha256 = hash, status = loaded ? "Pass" : "Fail", state = image.State.ToString(), nativeSurface = surface?.GetType().Name, framePath, networkRequested = false });
            }
            finally { window.Close(); await image.LoadingCompletion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
        }
        File.WriteAllText(Path.Combine(directory, "native-results.json"), JsonSerializer.Serialize(new { scope = "Offline native ImageView decode of explicitly accepted production-loader bytes", complete = true, uniquePayloadChecks = results.Count, results }, ReportOptions));
        Assert.NotEmpty(results); Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [AvaloniaFact]
    public async Task Every_corpus_image_records_actual_native_load_or_explicit_policy_block()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Tern.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var directory = Path.Combine(root.FullName, "artifacts", "rendering-validation");
        var frames = Path.Combine(directory, "tern", "images"); Directory.CreateDirectory(frames);
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "inventory.json")));
        var roots = inventory.RootElement.GetProperty("sources").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!, s => s.GetProperty("root").GetString()!);
        var reports = new List<object>(); var failures = new List<string>(); var decoded = 0;
        foreach (var file in inventory.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = file.GetProperty("localPath").GetString()!; var id = file.GetProperty("id").GetString()!;
            var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            var document = PreviewBuilder.Build(File.ReadAllText(path), new PreviewOptions { DocumentPath = path, WorkspaceRoot = roots[file.GetProperty("source").GetString()!] });
            foreach (var node in Walk(document.Blocks).OfType<ImageNode>())
            {
                if (node.Resolution is Blocked blocked)
                {
                    reports.Add(new { id, line = node.Source.StartLine, destination = node.Destination, alt = node.Alt, status = "Blocked", state = "PolicyBlocked", reason = blocked.Reason.ToString(), networkRequested = false });
                    continue;
                }
                using var image = new ImageView(node.Resolution, node.Alt, node.Title);
                var window = new Window { Width = 640, Height = 480, Content = image };
                try
                {
                    window.Show(); Harness.Pump(window);
                    await image.LoadingCompletion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                    Harness.Pump(window);
                    var nativeImages = Harness.All<Image>(image);
                    var nativeSurface = nativeImages.Any(i => i.Source is not null && i.Bounds.Width > 0 && i.Bounds.Height > 0)
                        || image.Child is { } svg && svg.GetType().Name == "SvgControl" && svg.Bounds.Width > 0 && svg.Bounds.Height > 0;
                    var loaded = image.State == ImageState.Loaded && nativeSurface;
                    using var capture = Harness.Capture(window);
                    loaded &= capture is not null;
                    var framePath = Path.Combine(frames, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id + ":" + node.Source.StartLine + ":" + node.Destination)))[..20] + ".png");
                    capture?.Save(framePath, new PngBitmapEncoderOptions());
                    if (!loaded) failures.Add(id + ":" + node.Source.StartLine + " " + node.Destination + " => " + image.State);
                    else decoded++;
                    reports.Add(new { id, line = node.Source.StartLine, destination = node.Destination, alt = node.Alt, status = loaded ? "Pass" : "Fail", state = image.State.ToString(), nativeImageControls = nativeImages.Count, nativeSurface = image.Child?.GetType().Name, frameCaptured = capture is not null, framePath, networkRequested = false });
                }
                finally { window.Close(); await image.LoadingCompletion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
            }
            Assert.Equal(originalHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        File.WriteAllText(Path.Combine(directory, "tern", "image-load-results.json"), JsonSerializer.Serialize(new { scope = "Actual ImageView native decode/render, no remote loader", complete = true, actualLoaded = decoded, results = reports }, ReportOptions));
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(decoded > 0, "The inventory must exercise actual allowed local or inline images.");
    }

    private static IEnumerable<PreviewNode> Walk(object? value)
    {
        if (value is null or string) yield break;
        if (value is PreviewNode node)
        {
            yield return node;
            foreach (var property in node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.GetIndexParameters().Length == 0))
                foreach (var child in Walk(property.GetValue(node))) yield return child;
        }
        else if (value is IEnumerable items) foreach (var item in items) foreach (var child in Walk(item)) yield return child;
    }
}
