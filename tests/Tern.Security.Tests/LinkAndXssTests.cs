using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Tern.App.Settings;
using Tern.App.Shell;
using Tern.Export;
using Tern.Rendering.Preview;
using Tern.Rendering.Resources;

namespace Tern.Security.Tests;

internal sealed class RecordingDialogs : IShellDialogs
{
    public bool Agree { get; set; }

    public int ExternalPrompts { get; private set; }

    public Task<SaveChoice> AskSaveChangesAsync(string documentName) => Task.FromResult(SaveChoice.Cancel);

    public Task<ConflictChoice> AskConflictAsync(Tern.App.Documents.DocumentTab tab) => Task.FromResult(ConflictChoice.Cancel);

    public Task<MissingFileChoice> AskFileMissingAsync(Tern.App.Documents.DocumentTab tab) => Task.FromResult(MissingFileChoice.KeepEditing);

    public Task<string?> ResolveMergeAsync(Tern.App.Documents.DocumentTab tab, Tern.Domain.Merging.MergeResult merge) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> PickFilesToOpenAsync() => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickSavePathAsync(string suggestedName, string? initialDirectory) => Task.FromResult<string?>(null);

    public Task<bool> ConfirmOverwriteAsync(string path) => Task.FromResult(false);

    public Task<bool> ConfirmOpenExternalAsync(Uri uri)
    {
        ExternalPrompts++;
        return Task.FromResult(Agree);
    }

    public void ShowMessage(string title, string message)
    {
    }
}

internal sealed class RecordingLauncher : IExternalLauncher
{
    public List<string> Opened { get; } = [];

    public void OpenUri(Uri uri) => Opened.Add(uri.AbsoluteUri);

    public void RevealInFileManager(string path) => Opened.Add(path);
}

public class LinkPolicyTests
{
    private static SafeResourceResolver Resolver(Sandbox box) =>
        new(new ResourceResolverOptions { DocumentDirectory = box.Workspace, WorkspaceRoot = box.Workspace });

    public static TheoryData<string> DangerousDestinations() => new()
    {
        "javascript:alert(1)",
        "JaVaScRiPt:alert(1)",
        " javascript:alert(1)",
        "java\tscript:alert(1)",
        "java\nscript:alert(1)",
        "vbscript:msgbox(1)",
        "data:text/html,<script>alert(1)</script>",
        "data:image/svg+xml;base64,PHN2Zz48c2NyaXB0Pg==",
        "file:///C:/Windows/System32/calc.exe",
        "file://server/share/x",
        "FILE:///etc/passwd",
        "\\\\server\\share\\payload.exe",
        "//server/share/payload.exe",
        "\\\\?\\C:\\Windows\\win.ini",
        "\\\\.\\PhysicalDrive0",
        "ms-msdt:/id PCWDiagnostic",
        "search-ms:query=x",
        "ms-excel:ofe|u|http://evil/x.xlsx",
        "ftp://evil/x",
        "ssh://evil",
        "smb://evil/share",
        "x-man-page://ls",
        "about:blank",
        "blob:http://x/y",
        "chrome://settings",
        "vscode://file/c:/x",
        "tel:+123",
        "intent://x#Intent;end",
        "mailto:a@b.c?body=%0d%0a",
    };

    [Theory]
    [MemberData(nameof(DangerousDestinations))]
    public void Dangerous_link_destinations_are_never_opened(string destination)
    {
        using var box = new Sandbox();
        var decision = Resolver(box).ResolveLink(destination);

        // mailto: is the one scheme the product deliberately allows (after confirmation); everything else here must be blocked.
        if (destination.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(decision is ExternalLink or Blocked);
            return;
        }

        Assert.IsType<Blocked>(decision);
    }

    [Theory]
    [MemberData(nameof(DangerousDestinations))]
    public void Dangerous_image_destinations_are_never_loaded(string destination)
    {
        using var box = new Sandbox();
        var decision = Resolver(box).ResolveImage(destination);
        Assert.False(decision is LocalFile or ExternalLink, destination);
    }

    [Theory]
    [InlineData("http://example.com/a.png")]
    [InlineData("https://example.com/a.png")]
    [InlineData("//example.com/a.png")]
    public void Remote_images_are_never_fetched(string destination)
    {
        using var box = new Sandbox();
        var decision = Resolver(box).ResolveImage(destination);
        Assert.IsType<Blocked>(decision);
    }

    [Fact]
    public void Data_images_are_bounded_and_only_raster_types_decode()
    {
        using var box = new Sandbox();
        var resolver = new SafeResourceResolver(new ResourceResolverOptions
        {
            DocumentDirectory = box.Workspace,
            WorkspaceRoot = box.Workspace,
            AllowDataImages = true,
            MaxDataImageBytes = 1024,
        });

        Assert.IsType<Blocked>(resolver.ResolveImage("data:image/svg+xml;utf8,<svg onload=alert(1)/>"));
        Assert.IsType<Blocked>(resolver.ResolveImage("data:text/html;base64,PHNjcmlwdD4="));
        Assert.IsType<Blocked>(resolver.ResolveImage("data:image/png;base64," + new string('A', 100_000)));
        Assert.IsType<Blocked>(resolver.ResolveImage("data:image/png;base64,!!!not-base64!!!"));
    }

    [Fact]
    public async Task Blocked_links_never_reach_the_launcher_or_the_confirmation_prompt()
    {
        using var box = new Sandbox();
        var dialogs = new RecordingDialogs { Agree = true };
        var launcher = new RecordingLauncher();
        using var shell = new ShellViewModel(AppPaths.Under(Path.Combine(box.Root, "appdata")), dialogs, launcher);
        var resolver = Resolver(box);

        foreach (var destination in new[] { "javascript:alert(1)", "file:///C:/Windows/notepad.exe", "\\\\server\\share\\x.exe", "data:text/html,x", "../outside/secret.md", "C:\\Windows\\notepad.exe" })
        {
            var result = await shell.ActivateLinkAsync(resolver.ResolveLink(destination));
            Assert.Equal(LinkOutcome.Blocked, result.Outcome);
        }

        Assert.Empty(launcher.Opened);
        Assert.Equal(0, dialogs.ExternalPrompts);
    }

    [Fact]
    public async Task External_links_need_confirmation_and_respect_the_settings_switch()
    {
        using var box = new Sandbox();
        var dialogs = new RecordingDialogs();
        var launcher = new RecordingLauncher();
        using var shell = new ShellViewModel(AppPaths.Under(Path.Combine(box.Root, "appdata")), dialogs, launcher);
        var decision = Resolver(box).ResolveLink("https://example.com/");

        Assert.Equal(LinkOutcome.DeclinedExternal, (await shell.ActivateLinkAsync(decision)).Outcome);
        Assert.Empty(launcher.Opened);

        dialogs.Agree = true;
        Assert.Equal(LinkOutcome.OpenedExternal, (await shell.ActivateLinkAsync(decision)).Outcome);
        Assert.Single(launcher.Opened);

        shell.Settings.AllowExternalLinkOpening = false;
        dialogs.Agree = true;
        Assert.Equal(LinkOutcome.DeclinedExternal, (await shell.ActivateLinkAsync(decision)).Outcome);
        Assert.Single(launcher.Opened);
    }

    [Fact]
    public async Task A_local_executable_is_never_launched_only_revealed_after_confirmation()
    {
        using var box = new Sandbox();
        var exe = box.Write("tool.exe", "MZ");
        var dialogs = new RecordingDialogs();
        var launcher = new RecordingLauncher();
        using var shell = new ShellViewModel(AppPaths.Under(Path.Combine(box.Root, "appdata")), dialogs, launcher);

        var declined = await shell.ActivateLinkAsync(Resolver(box).ResolveLink("tool.exe"));
        Assert.Equal(LinkOutcome.DeclinedExternal, declined.Outcome);
        Assert.Empty(launcher.Opened);

        dialogs.Agree = true;
        var accepted = await shell.ActivateLinkAsync(Resolver(box).ResolveLink("tool.exe"));
        Assert.Equal(LinkOutcome.OpenedWithSystem, accepted.Outcome);
        Assert.Equal(exe, Assert.Single(launcher.Opened), ignoreCase: true);
    }
}

public class XssTests
{
    private static readonly string[] Payloads =
    [
        "<script>alert(1)</script>",
        "<img src=x onerror=alert(1)>",
        "<svg onload=alert(1)>",
        "<a href=\"javascript:alert(1)\">x</a>",
        "<iframe src=\"https://evil\"></iframe>",
        "<object data=\"x\"></object><embed src=\"x\">",
        "<form action=\"https://evil\"><input></form>",
        "<style>@import 'https://evil';</style>",
        "<link rel=stylesheet href=\"https://evil\">",
        "<meta http-equiv=\"refresh\" content=\"0;url=https://evil\">",
        "<base href=\"https://evil/\">",
        "<div style=\"background:url(https://evil)\">x</div>",
        "<details open ontoggle=alert(1)>x</details>",
        "<math><mi xlink:href=\"javascript:alert(1)\">x</mi></math>",
        "<SCRIPT SRC=//evil></SCRIPT>",
        "<scr<script>ipt>alert(1)</scr</script>ipt>",
        "<img src=\"x\" alt=\"\" onerror=\"alert(1)\"/>",
        "<a href=\"&#106;avascript:alert(1)\">x</a>",
        "<a href=\"  javascript:alert(1)\">x</a>",
        "<body onload=alert(1)>",
        "<video src=x onerror=alert(1)>",
        "<!--[if IE]><script>alert(1)</script><![endif]-->",
        "<![CDATA[<script>alert(1)</script>]]>",
    ];

    private static readonly Regex RawEventHandler = new(@"<[a-zA-Z][^>]*\son[a-zA-Z]+\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RawDangerousTag = new(@"<\s*/?\s*(script|iframe|object|embed|form|input|link|meta|base|video|audio|style)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ActiveUrl = new(@"(href|src|action|formaction|xlink:href|srcset|data)\s*=\s*[""']?\s*(javascript|vbscript|data|file):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedValues = new("\"[^\"]*\"|'[^']*'", RegexOptions.CultureInvariant);

    private static string BodyOf(string html)
    {
        // The exporter's own template legitimately carries one <style> block and <meta> tags in the head.
        var start = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        return start < 0 ? html : html[start..];
    }

    private static void AssertInert(string html, string context)
    {
        var body = QuotedValues.Replace(BodyOf(html), "\"\"");
        Assert.False(RawEventHandler.IsMatch(body), $"Event handler survived ({context}).");
        Assert.False(RawDangerousTag.IsMatch(body), $"Dangerous tag survived ({context}).");
        Assert.False(ActiveUrl.IsMatch(BodyOf(html)), $"Active URL survived ({context}).");
    }

    [Fact]
    public void Exported_html_never_contains_executable_markup_from_raw_html_blocks()
    {
        foreach (var payload in Payloads)
        {
            var html = HtmlExporter.Export(payload + "\n").Html;
            AssertInert(html, payload);
            html = HtmlExporter.Export("text before " + payload + " text after\n\n" + payload + "\n").Html;
            AssertInert(html, "inline " + payload);
        }
    }

    [Fact]
    public void Exported_html_is_inert_for_hostile_markdown_constructs()
    {
        var cases = new[]
        {
            "[x](javascript:alert(1))",
            "[x](<javascript:alert(1)>)",
            "[x](JAVASCRIPT:alert(1) \"t\")",
            "![x](javascript:alert(1))",
            "![\" onerror=\"alert(1)](x.png)",
            "![x](x.png \"\" onerror=\"alert(1)\")",
            "[x](https://e.com \"\\\" onmouseover=\\\"alert(1)\")",
            "[\"><script>alert(1)</script>](a.md)",
            "# <script>alert(1)</script>",
            "# \"><img src=x onerror=alert(1)>",
            "```\"><script>alert(1)</script>\n<script>alert(1)</script>\n```",
            "```js onload=alert(1)\nx\n```",
            "`<script>alert(1)</script>`",
            "| <script>alert(1)</script> |\n|---|\n| <img src=x onerror=alert(1)> |",
            "$$ \\href{javascript:alert(1)}{x} $$",
            "$\\text{<script>alert(1)</script>}$",
            "```mermaid\ngraph TD\nA[\"<script>alert(1)</script>\"] --> B[<img src=x onerror=alert(1)>]\n```",
            "```mermaid\nsequenceDiagram\nparticipant A as <script>alert(1)</script>\nA->>A: <img src=x onerror=alert(1)>\n```",
            "---\ntitle: \"</title><script>alert(1)</script>\"\ntags: [<img src=x onerror=alert(1)>]\n---\nbody",
            "[ref]: javascript:alert(1)\n\n[x][ref]",
            "[^1]: <script>alert(1)</script>\n\ntext[^1]",
            "<https://e.com/\"onmouseover=\"alert(1)>",
            "[[<script>alert(1)</script>]]",
            "- [ ] <img src=x onerror=alert(1)>",
            "> [!NOTE]\n> <script>alert(1)</script>",
            "<details><summary><img src=x onerror=alert(1)></summary>x</details>",
        };

        foreach (var markdown in cases)
        {
            var result = HtmlExporter.Export(markdown + "\n");
            AssertInert(result.Html, markdown);
            Assert.DoesNotContain("</title><script", result.Html, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_html_title_is_escaped()
    {
        var html = HtmlExporter.Export("# <img src=x onerror=alert(1)>\n").Html;
        var start = html.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
        Assert.True(start >= 0);
        var end = html.IndexOf("</title>", start, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", html[(start + 7)..end]);
        Assert.DoesNotContain("<script", HtmlExporter.Export("---\ntitle: \"</title><script>alert(1)</script>\"\n---\nx\n").Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exported_html_loads_no_remote_resources()
    {
        var html = HtmlExporter.Export("![a](https://evil/x.png)\n\n![b](//evil/x.png)\n\n[c](https://evil/)\n").Html;
        Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"//", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(http", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preview_html_blocks_hold_only_sanitised_markup()
    {
        foreach (var payload in Payloads)
        {
            var document = PreviewBuilder.Build(payload + "\n\ntext " + payload + "\n", new PreviewOptions());
            foreach (var node in Walk(document.Blocks).OfType<HtmlSafeBlockNode>())
            {
                AssertInert(node.Html, payload);
            }
        }
    }

    [Fact]
    public void Preview_links_and_images_always_carry_a_policy_decision()
    {
        using var box = new Sandbox();
        var resolver = new SafeResourceResolver(new ResourceResolverOptions { DocumentDirectory = box.Workspace, WorkspaceRoot = box.Workspace });
        var markdown = "[a](javascript:alert(1)) [b](file:///C:/x) [c](//h/s/x) ![d](data:text/html,x) ![e](http://evil/x.png) [f](../outside/secret.md)\n";
        var document = PreviewBuilder.Build(markdown, new PreviewOptions(), resolver);

        var nodes = Walk(document.Blocks).ToList();
        var links = nodes.OfType<LinkNode>().ToList();
        var images = nodes.OfType<ImageNode>().ToList();

        Assert.Equal(4, links.Count);
        Assert.Equal(2, images.Count);
        Assert.All(links, l => Assert.IsType<Blocked>(l.Resolution));
        Assert.All(images, i => Assert.IsType<Blocked>(i.Resolution));
    }

    private static IEnumerable<object> Walk(object? value, int depth = 0)
    {
        if (value is null || depth > 64)
        {
            yield break;
        }

        if (value is string)
        {
            yield break;
        }

        if (value is IEnumerable sequence)
        {
            foreach (var item in sequence)
            {
                foreach (var found in Walk(item, depth + 1))
                {
                    yield return found;
                }
            }

            yield break;
        }

        if (value is PreviewNode)
        {
            yield return value;
            foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                var type = property.PropertyType;
                if (typeof(PreviewNode).IsAssignableFrom(type) || (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string)))
                {
                    foreach (var found in Walk(property.GetValue(value), depth + 1))
                    {
                        yield return found;
                    }
                }
            }
        }
    }
}
