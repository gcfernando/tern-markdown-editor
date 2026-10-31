using System.Diagnostics;
using Tern.Rendering.Resources;
using Xunit.Abstractions;

namespace Tern.Rendering.Tests;

public sealed class ResolverFixture : IDisposable
{
    private readonly WorkDirectory _work = new();

    public ResolverFixture()
    {
        Workspace = _work.Combine("ws");
        Docs = _work.Combine("ws", "docs");
        Outside = _work.Combine("outside");
        _work.Write("ws/docs/pic.png", SampleImages.Png);
        _work.Write("ws/docs/sub/deep.png", SampleImages.Png);
        _work.Write("ws/docs/my pic.png", SampleImages.Png);
        _work.Write("ws/docs/ünï-图.png", SampleImages.Png);
        _work.Write("ws/docs/note.md", "# note");
        _work.Write("ws/docs/vector.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        _work.Write("ws/docs/icon.ICO", SampleImages.Png);
        _work.Write("ws/x.png", SampleImages.Png);
        _work.Write("ws/shared/s.png", SampleImages.Png);
        _work.Write("ws/docs/script.exe", "MZ");
        _work.Write("outside/secret.png", SampleImages.Png);
        _work.Write("outside/secret.md", "secret");
        _work.Write("outside/dir/inner.png", SampleImages.Png);

        using (var big = new FileStream(_work.Combine("ws", "docs", "big.png"), FileMode.Create))
        {
            big.SetLength((20L * 1024 * 1024) + 1);
        }

        using (var exact = new FileStream(_work.Combine("ws", "docs", "exact.png"), FileMode.Create))
        {
            exact.SetLength(20L * 1024 * 1024);
        }
    }

    public string Workspace { get; }

    public string Docs { get; }

    public string Outside { get; }

    public string Combine(params string[] parts) => _work.Combine(parts);

    public SafeResourceResolver Create(Func<ResourceResolverOptions, ResourceResolverOptions>? configure = null)
    {
        var options = new ResourceResolverOptions { DocumentDirectory = Docs, WorkspaceRoot = Workspace };
        return new SafeResourceResolver(configure is null ? options : configure(options));
    }

    public void Dispose() => _work.Dispose();
}

public sealed class ResolverTests(ResolverFixture fixture, ITestOutputHelper output) : IClassFixture<ResolverFixture>
{
    private static bool SamePath(string expected, string actual) =>
        string.Equals(Path.GetFullPath(expected), actual, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void AssertBlocked(ResourceDecision decision, BlockReason reason)
    {
        var blocked = Assert.IsType<Blocked>(decision);
        Assert.Equal(reason, blocked.Reason);
    }

    public static TheoryData<string, BlockReason> MaliciousLinks => new()
    {
        { "javascript:alert(1)", BlockReason.ScriptScheme },
        { "JaVaScRiPt:alert(1)", BlockReason.ScriptScheme },
        { "  javascript:alert(document.domain)", BlockReason.ScriptScheme },
        { "vbscript:msgbox(1)", BlockReason.ScriptScheme },
        { "VBScript:Execute(\"x\")", BlockReason.ScriptScheme },
        { "livescript:alert(1)", BlockReason.ScriptScheme },
        { "java\tscript:alert(1)", BlockReason.ControlCharacters },
        { "java\nscript:alert(1)", BlockReason.ControlCharacters },
        { "javascript\u0000:alert(1)", BlockReason.ControlCharacters },
        { "file:///C:/Windows/win.ini", BlockReason.FileUrl },
        { "FILE://server/share/x", BlockReason.FileUrl },
        { "data:text/html,<script>alert(1)</script>", BlockReason.DataUrl },
        { "data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==", BlockReason.DataUrl },
        { "data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=", BlockReason.DataUrl },
        { "ftp://example.com/file", BlockReason.DisallowedScheme },
        { "ssh://host/", BlockReason.DisallowedScheme },
        { "tel:+123456", BlockReason.DisallowedScheme },
        { "ms-msdt:/id PCWDiagnostic", BlockReason.DisallowedScheme },
        { "search-ms:query=x", BlockReason.DisallowedScheme },
        { "\\\\server\\share\\x.md", BlockReason.UncPath },
        { "//server/share/x.md", BlockReason.UncPath },
        { "\\/server/share", BlockReason.UncPath },
        { "/\\server/share", BlockReason.UncPath },
        { "\\\\.\\C:\\Windows", BlockReason.DevicePath },
        { "\\\\?\\C:\\Windows", BlockReason.DevicePath },
        { "//./PhysicalDrive0", BlockReason.DevicePath },
        { "note.md:stream", BlockReason.AlternateDataStream },
        { "note.md::$DATA", BlockReason.AlternateDataStream },
        { "note.md:Zone.Identifier", BlockReason.AlternateDataStream },
        { "C:foo.md", BlockReason.AlternateDataStream },
        { "CON", BlockReason.DevicePath },
        { "docs/NUL.md", BlockReason.DevicePath },
        { "com1.txt", BlockReason.DevicePath },
        { "../../outside/secret.md", BlockReason.Traversal },
        { "..\\..\\outside\\secret.md", BlockReason.Traversal },
        { "../../../../../../../../Windows/win.ini", BlockReason.Traversal },
        { "sub/../../../outside/secret.md", BlockReason.Traversal },
        { "%2e%2e/%2e%2e/outside/secret.md", BlockReason.Traversal },
        { "%2E%2E%5C%2E%2E%5Coutside%5Csecret.md", BlockReason.Traversal },
        { "..%2f..%2foutside%2fsecret.md", BlockReason.Traversal },
        { "%252e%252e/%252e%252e/outside/secret.md", BlockReason.InvalidPath },
        { "..%255c..%255coutside", BlockReason.InvalidPath },
        { "%25252e%25252e/x", BlockReason.InvalidPath },
        { "a%00b.md", BlockReason.ControlCharacters },
        { "a\u0001b.md", BlockReason.ControlCharacters },
        { "a\u007fb.md", BlockReason.ControlCharacters },
        { "", BlockReason.Empty },
        { "   ", BlockReason.Empty },
    };

    [Theory]
    [MemberData(nameof(MaliciousLinks))]
    public void Links_that_are_dangerous_are_blocked(string destination, BlockReason reason)
    {
        AssertBlocked(fixture.Create().ResolveLink(destination), reason);
    }

    [Theory]
    [MemberData(nameof(MaliciousLinks))]
    public void Images_that_are_dangerous_are_blocked(string destination, BlockReason reason)
    {
        var decision = fixture.Create().ResolveImage(destination);

        var blocked = Assert.IsType<Blocked>(decision);
        // Images use the same path rules as links; only the reason for data: URLs differs when data images are enabled.
        Assert.True(blocked.Reason == reason || (reason == BlockReason.DataUrl && blocked.Reason == BlockReason.DataUrl));
    }

    [Fact]
    public void Absolute_path_outside_the_roots_is_blocked()
    {
        var outside = fixture.Combine("outside", "secret.md").Replace('\\', '/');

        AssertBlocked(fixture.Create().ResolveLink(outside), BlockReason.OutsideAllowedRoots);
        AssertBlocked(fixture.Create().ResolveImage(fixture.Combine("outside", "secret.png")), BlockReason.OutsideAllowedRoots);
    }

    [Fact]
    public void Absolute_path_inside_a_root_is_allowed()
    {
        var inside = fixture.Combine("ws", "docs", "note.md");

        var decision = Assert.IsType<LocalFile>(fixture.Create().ResolveLink(inside));

        Assert.True(SamePath(inside, decision.CanonicalPath));
    }

    [Fact]
    public void Windows_drive_paths_outside_the_roots_are_blocked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        AssertBlocked(fixture.Create().ResolveLink("C:/Windows/win.ini"), BlockReason.OutsideAllowedRoots);
        AssertBlocked(fixture.Create().ResolveLink("C:\\Windows\\win.ini"), BlockReason.OutsideAllowedRoots);
    }

    [Fact]
    public void Relative_paths_without_any_base_directory_are_refused()
    {
        var resolver = new SafeResourceResolver(new ResourceResolverOptions());

        AssertBlocked(resolver.ResolveLink("note.md"), BlockReason.NoBaseDirectory);
        AssertBlocked(resolver.ResolveLink("/note.md"), BlockReason.NoBaseDirectory);
    }

    [Fact]
    public void Over_long_paths_are_blocked()
    {
        var resolver = fixture.Create();

        AssertBlocked(resolver.ResolveLink(new string('a', 2000) + ".md"), BlockReason.PathTooLong);
        AssertBlocked(resolver.ResolveLink(new string('a', 9000)), BlockReason.PathTooLong);
        AssertBlocked(resolver.ResolveLink("http://example.com/" + new string('a', 9000)), BlockReason.PathTooLong);
    }

    [Fact]
    public void Only_the_document_directory_is_a_root_when_no_workspace_is_given()
    {
        var resolver = new SafeResourceResolver(new ResourceResolverOptions { DocumentDirectory = fixture.Docs });

        AssertBlocked(resolver.ResolveLink("../x.png"), BlockReason.Traversal);
        Assert.IsType<LocalFile>(resolver.ResolveLink("sub/deep.png"));
    }

    [Fact]
    public void Additional_roots_extend_what_is_allowed()
    {
        var resolver = fixture.Create(o => o with { AdditionalRoots = [fixture.Combine("outside")] });

        var decision = Assert.IsType<LocalFile>(resolver.ResolveImage(fixture.Combine("outside", "secret.png")));

        Assert.True(SamePath(fixture.Combine("outside", "secret.png"), decision.CanonicalPath));
    }

    [Fact]
    public void A_sibling_folder_sharing_a_root_prefix_is_not_inside_the_root()
    {
        var resolver = new SafeResourceResolver(new ResourceResolverOptions { DocumentDirectory = fixture.Docs });
        var sibling = fixture.Docs + "-evil/x.md";

        AssertBlocked(resolver.ResolveLink(sibling.Replace('\\', '/')), BlockReason.OutsideAllowedRoots);
    }

    [Theory]
    [InlineData("http://example.com/a.png")]
    [InlineData("https://example.com/a.png?x=1#y")]
    [InlineData("HTTPS://EXAMPLE.COM/A.PNG")]
    public void Remote_images_are_never_loaded_and_keep_their_url(string url)
    {
        var blocked = Assert.IsType<Blocked>(fixture.Create().ResolveImage(url));

        Assert.Equal(BlockReason.RemoteResourceBlocked, blocked.Reason);
        Assert.Equal(url, blocked.Original);
    }

    [Fact]
    public void Protocol_relative_image_urls_are_blocked_as_network_paths()
    {
        AssertBlocked(fixture.Create().ResolveImage("//cdn.example.com/a.png"), BlockReason.UncPath);
    }

    [Fact]
    public void Mailto_cannot_be_an_image()
    {
        AssertBlocked(fixture.Create().ResolveImage("mailto:a@b.c"), BlockReason.DisallowedScheme);
    }

    [Fact]
    public void Images_with_wrong_extensions_are_blocked()
    {
        var resolver = fixture.Create();

        AssertBlocked(resolver.ResolveImage("note.md"), BlockReason.UnsupportedImageType);
        AssertBlocked(resolver.ResolveImage("script.exe"), BlockReason.UnsupportedImageType);
        AssertBlocked(resolver.ResolveImage("pic"), BlockReason.UnsupportedImageType);
        AssertBlocked(resolver.ResolveImage("pic.png.exe"), BlockReason.UnsupportedImageType);
    }

    [Fact]
    public void Missing_images_are_reported_as_not_found()
    {
        AssertBlocked(fixture.Create().ResolveImage("nothing.png"), BlockReason.NotFound);
    }

    [Fact]
    public void Images_over_the_size_limit_are_blocked()
    {
        var resolver = fixture.Create();

        AssertBlocked(resolver.ResolveImage("big.png"), BlockReason.FileTooLarge);
        Assert.IsType<LocalFile>(resolver.ResolveImage("exact.png"));
    }

    [Fact]
    public void The_size_limit_is_configurable()
    {
        var resolver = fixture.Create(o => o with { MaxImageBytes = 10 });

        AssertBlocked(resolver.ResolveImage("pic.png"), BlockReason.FileTooLarge);
    }

    [Fact]
    public void Svg_images_are_returned_but_flagged_as_active_content()
    {
        var resolver = fixture.Create();

        var svg = Assert.IsType<LocalFile>(resolver.ResolveImage("vector.svg"));
        var png = Assert.IsType<LocalFile>(resolver.ResolveImage("pic.png"));

        Assert.True(svg.IsActiveContentRisk);
        Assert.False(png.IsActiveContentRisk);
    }

    [Fact]
    public void Image_extensions_are_matched_case_insensitively()
    {
        Assert.IsType<LocalFile>(fixture.Create().ResolveImage("icon.ico"));
    }

    [Fact]
    public void Valid_png_data_images_are_decoded()
    {
        var decision = Assert.IsType<InlineImage>(fixture.Create().ResolveImage(SampleImages.PngDataUri));

        Assert.Equal("image/png", decision.MediaType);
        Assert.Equal(SampleImages.Png, decision.Data);
    }

    [Fact]
    public void Data_images_can_be_disabled()
    {
        var resolver = fixture.Create(o => o with { AllowDataImages = false });

        AssertBlocked(resolver.ResolveImage(SampleImages.PngDataUri), BlockReason.DataUrl);
    }

    [Fact]
    public void Data_images_over_the_size_cap_are_blocked()
    {
        var resolver = fixture.Create(o => o with { MaxDataImageBytes = 16 });

        AssertBlocked(resolver.ResolveImage(SampleImages.PngDataUri), BlockReason.DataUrl);
    }

    [Theory]
    [InlineData("data:image/png;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("data:image/png;base64,!!!notbase64!!!")]
    [InlineData("data:image/png;base64,")]
    [InlineData("data:image/png,rawbytes")]
    [InlineData("data:image/gif;base64,iVBORw0KGgo=")]
    public void Data_images_whose_payload_does_not_match_are_blocked(string uri)
    {
        var blocked = Assert.IsType<Blocked>(fixture.Create().ResolveImage(uri));

        Assert.Contains(blocked.Reason, new[] { BlockReason.InvalidDataUri, BlockReason.DataUrl });
    }

    [Fact]
    public void Svg_data_images_are_never_accepted()
    {
        AssertBlocked(fixture.Create().ResolveImage("data:image/svg+xml;base64,PHN2ZyBvbmxvYWQ9YWxlcnQoMSk+"), BlockReason.DataUrl);
        AssertBlocked(fixture.Create().ResolveImage("data:image/svg+xml,<svg onload=alert(1)>"), BlockReason.DataUrl);
    }

    [Fact]
    public void Data_urls_are_never_acceptable_links()
    {
        AssertBlocked(fixture.Create().ResolveLink(SampleImages.PngDataUri), BlockReason.DataUrl);
    }

    [Theory]
    [InlineData("note.md", "note.md")]
    [InlineData("./note.md", "note.md")]
    [InlineData("sub/deep.png", "sub/deep.png")]
    [InlineData("sub\\deep.png", "sub/deep.png")]
    [InlineData("my%20pic.png", "my pic.png")]
    [InlineData("my pic.png", "my pic.png")]
    [InlineData("%C3%BCn%C3%AF-%E5%9B%BE.png", "ünï-图.png")]
    [InlineData("ünï-图.png", "ünï-图.png")]
    [InlineData("sub/../note.md", "note.md")]
    [InlineData("../shared/s.png", "../shared/s.png")]
    public void Relative_links_resolve_inside_the_document_directory(string destination, string expectedRelative)
    {
        var expected = Path.Combine(fixture.Docs, expectedRelative.Replace('/', Path.DirectorySeparatorChar));

        var decision = Assert.IsType<LocalFile>(fixture.Create().ResolveLink(destination));

        Assert.True(SamePath(expected, decision.CanonicalPath), decision.CanonicalPath);
        Assert.True(decision.Exists);
    }

    [Theory]
    [InlineData("pic.png")]
    [InlineData("sub/deep.png")]
    [InlineData("my%20pic.png")]
    [InlineData("ünï-图.png")]
    [InlineData("../shared/s.png")]
    [InlineData("pic.png?raw=true")]
    public void Existing_images_resolve(string destination)
    {
        var decision = Assert.IsType<LocalFile>(fixture.Create().ResolveImage(destination));

        Assert.True(File.Exists(decision.CanonicalPath));
        Assert.False(decision.IsActiveContentRisk);
    }

    [Fact]
    public void Missing_local_links_are_returned_with_Exists_false()
    {
        var decision = Assert.IsType<LocalFile>(fixture.Create().ResolveLink("not-written-yet.md"));

        Assert.False(decision.Exists);
    }

    [Fact]
    public void Fragments_are_split_from_file_links()
    {
        var decision = Assert.IsType<LocalFile>(fixture.Create().ResolveLink("note.md#Some%20Heading"));

        Assert.Equal("Some Heading", decision.Fragment);
        Assert.EndsWith("note.md", decision.CanonicalPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pure_fragments_are_anchors()
    {
        Assert.Equal("top", Assert.IsType<Anchor>(fixture.Create().ResolveLink("#top")).Fragment);
        Assert.Equal("my heading", Assert.IsType<Anchor>(fixture.Create().ResolveLink("#my%20heading")).Fragment);
    }

    [Fact]
    public void An_image_cannot_be_a_fragment()
    {
        AssertBlocked(fixture.Create().ResolveImage("#top"), BlockReason.InvalidPath);
    }

    [Fact]
    public void Root_relative_paths_resolve_against_the_workspace_root()
    {
        var decision = Assert.IsType<LocalFile>(fixture.Create().ResolveImage("/x.png"));

        Assert.True(SamePath(fixture.Combine("ws", "x.png"), decision.CanonicalPath));
    }

    [Fact]
    public void Root_relative_traversal_cannot_leave_the_workspace()
    {
        var resolver = fixture.Create();

        AssertBlocked(resolver.ResolveLink("/../outside/secret.md"), BlockReason.Traversal);
        AssertBlocked(resolver.ResolveLink("/%2e%2e/outside/secret.md"), BlockReason.Traversal);
    }

    [Theory]
    [InlineData("https://example.com/path?q=1#frag")]
    [InlineData("http://example.com")]
    [InlineData("HTTP://EXAMPLE.COM/")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("mailto:someone@example.com?subject=Hi%20there")]
    public void Web_and_mail_links_are_external_and_unmodified(string url)
    {
        var decision = Assert.IsType<ExternalLink>(fixture.Create().ResolveLink(url));

        Assert.Equal(url, decision.Original);
        Assert.Contains(decision.Uri.Scheme, new[] { "http", "https", "mailto" });
    }

    [Theory]
    [InlineData("http://")]
    [InlineData("https:///nohost")]
    public void Malformed_web_urls_are_blocked(string url)
    {
        Assert.IsType<Blocked>(fixture.Create().ResolveLink(url));
    }

    [Fact]
    public void Roots_are_exposed_as_full_paths()
    {
        var roots = fixture.Create().Roots;

        Assert.Contains(roots, r => SamePath(fixture.Workspace, r));
        Assert.Contains(roots, r => SamePath(fixture.Docs, r));
    }

    [Fact]
    public void A_file_symlink_pointing_outside_is_blocked()
    {
        var link = fixture.Combine("ws", "docs", "link-to-secret.png");
        if (!TryCreateFileSymlink(link, fixture.Combine("outside", "secret.png")))
        {
            return;
        }

        AssertBlocked(fixture.Create().ResolveImage("link-to-secret.png"), BlockReason.SymlinkEscape);
        AssertBlocked(fixture.Create().ResolveLink("link-to-secret.png"), BlockReason.SymlinkEscape);
    }

    [Fact]
    public void A_directory_symlink_pointing_outside_is_blocked()
    {
        var link = fixture.Combine("ws", "docs", "link-dir");
        if (!TryCreateDirectorySymlink(link, fixture.Combine("outside", "dir")))
        {
            return;
        }

        AssertBlocked(fixture.Create().ResolveImage("link-dir/inner.png"), BlockReason.SymlinkEscape);
    }

    [Fact]
    public void A_symlink_pointing_inside_the_roots_is_allowed_and_canonicalised()
    {
        var link = fixture.Combine("ws", "docs", "link-to-pic.png");
        if (!TryCreateFileSymlink(link, fixture.Combine("ws", "x.png")))
        {
            return;
        }

        var decision = Assert.IsType<LocalFile>(fixture.Create().ResolveImage("link-to-pic.png"));

        Assert.True(SamePath(fixture.Combine("ws", "x.png"), decision.CanonicalPath), decision.CanonicalPath);
    }

    [Fact]
    public void A_symlink_whose_name_is_innocent_but_target_is_not_an_image_is_blocked()
    {
        var link = fixture.Combine("ws", "docs", "disguised.png");
        if (!TryCreateFileSymlink(link, fixture.Combine("ws", "docs", "note.md")))
        {
            return;
        }

        AssertBlocked(fixture.Create().ResolveImage("disguised.png"), BlockReason.UnsupportedImageType);
    }

    [Fact]
    public void A_chain_of_symlinks_ending_outside_is_blocked()
    {
        var second = fixture.Combine("ws", "docs", "hop2.png");
        var first = fixture.Combine("ws", "docs", "hop1.png");
        if (!TryCreateFileSymlink(second, fixture.Combine("outside", "secret.png")) || !TryCreateFileSymlink(first, second))
        {
            return;
        }

        AssertBlocked(fixture.Create().ResolveImage("hop1.png"), BlockReason.SymlinkEscape);
    }

    [Fact]
    public void A_junction_pointing_outside_is_blocked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var junction = fixture.Combine("ws", "docs", "junction");
        var started = RunCmd($"mklink /J \"{junction}\" \"{fixture.Combine("outside", "dir")}\"");
        Assert.True(started, "mklink /J must work without elevation on Windows.");

        AssertBlocked(fixture.Create().ResolveImage("junction/inner.png"), BlockReason.SymlinkEscape);
        AssertBlocked(fixture.Create().ResolveLink("junction/inner.png"), BlockReason.SymlinkEscape);
    }

    [Fact]
    public void A_junction_pointing_inside_the_roots_is_allowed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var junction = fixture.Combine("ws", "docs", "junction-inside");
        Assert.True(RunCmd($"mklink /J \"{junction}\" \"{fixture.Combine("ws", "shared")}\""));

        var decision = Assert.IsType<LocalFile>(fixture.Create().ResolveImage("junction-inside/s.png"));

        Assert.True(SamePath(fixture.Combine("ws", "shared", "s.png"), decision.CanonicalPath), decision.CanonicalPath);
    }

    private bool TryCreateFileSymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            output.WriteLine($"UNMET prerequisite: file symlink creation ({ex.GetType().Name}).");
            throw new Xunit.Sdk.XunitException("UNMET prerequisite: enable file symlink creation for the resolver acceptance suite. This case was not exercised.");
        }
    }

    private bool TryCreateDirectorySymlink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            output.WriteLine($"UNMET prerequisite: directory symlink creation ({ex.GetType().Name}).");
            throw new Xunit.Sdk.XunitException("UNMET prerequisite: enable directory symlink creation for the resolver acceptance suite. This case was not exercised.");
        }
    }

    private static bool RunCmd(string command)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }
}
