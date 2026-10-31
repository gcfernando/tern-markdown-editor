using System.Security.Cryptography;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Tern.Rendering.Preview;

namespace Tern.PreviewView.Tests;

/// <summary>
/// Actual excerpts from Gehan Fernando's developer-guides-and-cheatsheets, retrieved 2026-10-26.
/// Source: https://github.com/gcfernando/developer-guides-and-cheatsheets/tree/main
/// Set TERN_PARITY_CORPUS to the downloaded root Markdown files to exercise complete documents instead;
/// set TERN_PARITY_SCREENSHOTS to retain deterministic review images. Neither option permits networking.
/// </summary>
public class RepositoryParityTests
{
    // SHA-256 of the complete downloaded source files, not of these short excerpts.
    private static readonly Dictionary<string, string> SourceHashes = new(StringComparer.Ordinal)
    {
        ["python_dunder.md"] = "9AC0A542896F60D6F951AA128760574DAE4820D3C93B44DAD2E2AABC729BC5F8",
        ["README.md"] = "89EAFEAE93BE8EC0527E1381D6608965EA8046A79D419C34536A888F705B0DFA",
        ["azure_cheatsheet.md"] = "B869B468E9975DAC21F579277E34D2E367E840DAE816D16A48F6A0FAD910DCDE",
    };

    private const string Python = """
        ---
        title: "Python Dunder Methods Reference: Special Methods and Attributes"
        description: "Understand Python special methods and attributes for object creation, operators, containers, descriptors, async behavior and serialization."
        ---

        <div align="center" markdown="1">

        <a id="-the-complete-bible-of-python-dunder-methods"></a>

        # Python Dunder Methods Reference — Special Methods and Attributes

        <a id="every-special-method-and-special-attribute-python-offers--explained-from-first-principles-with-runnable-examples"></a>

        *Python special methods and attributes explained through contracts, examples and exercises.*

        **_By Gehan Fernando_**

        ![Python](https://img.shields.io/badge/Python-Object%20model-3776AB?style=for-the-badge&logo=python&logoColor=white)
        ![Entries](https://img.shields.io/badge/Entries-145-F59E0B?style=for-the-badge)

        </div>

        ---

        ## 🎯 A Note Before You Begin

        This is a long guide, but it is built to be used, not endured. It works two ways:

        - **As a course.** Read Part 1, then Parts A through D in order. Those four parts carry every idea the rest of the guide builds on.
        - **As a reference.** After that, jump anywhere. Each entry is self-contained and states what it assumes.

        Every entry answers the same questions:

        - 🎨 **A real-world story** — an analogy to anchor the idea (with its limits stated, because analogies leak).
        - 🔍 **When to use it** — the situations where the method actually earns its place.
        - 💻 **How to use it** — code you can paste into a file and run.
        - 🧾 **The contract** — what triggers it, what arguments it receives, what it must return, and what happens on failure.
        - ⚠️ **Traps** — the mistakes that produce confusing bugs, and how to recognise them.

        ### What you need to know first

        This guide assumes you can write and run a basic Python class:

        ```python
        class Dog:
            def __init__(self, name):
                self.name = name

            def speak(self):
                return f"{self.name} says woof"
        ```
        """;

    private const string Readme = """
        ## 🏗️ Architecture

        <a id="study-map"></a>

        ```mermaid
        flowchart LR
            Index[README and documentation index] --> Language[Python or C# fundamentals]
            Language --> SQL[SQL and application data]
            SQL --> Cloud[Azure decisions and resources]
            Index --> Tools[Git, Docker and Podman references]
            Index --> Lab[Local AI chat lab]
        ```
        """;

    private const string Azure = """
        <a id="s1"></a>
        ## 1. 🧠 Azure Mental Model & Global Infrastructure

        ### Azure in one picture

        ```mermaid
        flowchart TB
            T[Microsoft Entra tenant] --> MG[Management Groups]
            MG --> S[Subscriptions]
            S --> RG[Resource Groups]
            RG --> R[Resources]
            R --> C[Compute]
            R --> D[Data]
            R --> N[Networking]
            R --> I[Identity]
            R --> O[Observability]
        ```

        #### 🧠 Remember this
        """;

    [AvaloniaTheory]
    [InlineData(800, false)]
    [InlineData(1200, false)]
    [InlineData(800, true)]
    [InlineData(1200, true)]
    public async Task Actual_repository_documents_render_at_two_reading_widths_in_both_themes(int width, bool paper)
    {
        var application = Application.Current!;
        var original = application.Resources.MergedDictionaries.ToArray();
        application.Resources.MergedDictionaries.Clear();
        application.Resources.MergedDictionaries.Add(paper ? PreviewTheme.Paper() : PreviewTheme.Midnight());
        try
        {
            foreach (var (name, fallback) in new[] { ("python_dunder.md", Python), ("README.md", Readme), ("azure_cheatsheet.md", Azure) })
            {
                var source = ReadSource(name, fallback);
                var corpusRoot = Environment.GetEnvironmentVariable("TERN_PARITY_CORPUS");
                var document = Harness.Build(source, new PreviewOptions { DocumentPath = corpusRoot is null ? null : Path.Combine(corpusRoot, name), WorkspaceRoot = corpusRoot });
                Assert.NotEmpty(document.Blocks);
                var (window, view) = Harness.Show(document, width, 1000);
                try
                {
                    view.FontSize = 16;
                    view.FontFamily = new FontFamily("Segoe UI, Helvetica, Arial, sans-serif");
                    if (Environment.GetEnvironmentVariable("TERN_PARITY_ASSETS") is { } assetRoot)
                    {
                        view.RemoteImageLoader = async (url, token) =>
                        {
                            var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(new Uri(url).AbsoluteUri))).ToLowerInvariant();
                            var path = Path.Combine(assetRoot, key + ".img");
                            return File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : null;
                        };
                    }
                    Harness.Pump(window, 8);
                    await WaitForImages(view);
                    var prefix = Path.GetFileNameWithoutExtension(name).ToLowerInvariant() + "-" + (paper ? "paper" : "midnight") + "-" + width;
                    if (name == "python_dunder.md")
                    {
                        Save(window, prefix + "-header.png");
                        view.ScrollToSourceLine(FindLine(source, "```python"));
                        Harness.Pump(window, 8);
                        await WaitForImages(view);
                        var code = Assert.Single(Harness.All<CodeBlockView>(view), c => c.Code.Contains("class Dog:", StringComparison.Ordinal));
                        Assert.Contains(code.CodeText.Inlines!.OfType<Avalonia.Controls.Documents.Run>(), r => r.Text == "class");
                        Save(window, prefix + "-code.png");
                    }
                    else
                    {
                        view.ScrollToSourceLine(FindLine(source, name == "README.md" ? "## 🏗️ Architecture" : "## 1. 🧠 Azure Mental Model"));
                        Harness.Pump(window, 8);
                        await WaitForImages(view);
                        Assert.NotEmpty(Harness.All<DiagramControl>(view));
                        Save(window, prefix + "-diagram.png");
                    }
                }
                finally { window.Close(); }
            }
        }
        finally
        {
            application.Resources.MergedDictionaries.Clear();
            foreach (var resources in original) application.Resources.MergedDictionaries.Add(resources);
        }
    }

    private static async Task WaitForImages(PreviewView view)
    {
        for (var i = 0; i < 300 && Harness.All<ImageView>(view).Any(image => image.State == ImageState.Loading); i++) await Task.Delay(10);
        Assert.DoesNotContain(Harness.All<ImageView>(view), image => image.State == ImageState.Loading);
        if (Environment.GetEnvironmentVariable("TERN_PARITY_ASSETS") is not null)
            Assert.All(Harness.All<ImageView>(view), image => Assert.Equal(ImageState.Loaded, image.State));
    }

    private static string ReadSource(string name, string fallback)
    {
        var corpus = Environment.GetEnvironmentVariable("TERN_PARITY_CORPUS");
        if (string.IsNullOrWhiteSpace(corpus)) return fallback;
        var path = Path.Combine(corpus, name);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(SourceHashes[name], Convert.ToHexString(SHA256.HashData(bytes)));
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static int FindLine(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "Expected source marker: " + marker);
        return 1 + source.AsSpan(0, index).Count('\n');
    }

    private static void Save(Avalonia.Controls.Window window, string name)
    {
        var output = Environment.GetEnvironmentVariable("TERN_PARITY_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        using var frame = Harness.Capture(window);
        Assert.NotNull(frame);
        Assert.True(Harness.NonBackgroundPixels(frame, new PixelRect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height)) > 500);
        using var stream = File.Create(Path.Combine(output, name));
        frame.Save(stream, new PngBitmapEncoderOptions());
    }
}
