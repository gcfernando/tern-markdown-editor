using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Reflection.Emit;
using System.Buffers.Binary;

namespace Tern.Security.Tests;

/// <summary>
/// Core editing remains offline. The user explicitly enabled real remote image retrieval;
/// only reviewed Desktop RemoteImageLoader and explicit optional AIConnectionClient
/// may use their separate, narrowly allowed BCL network APIs.
/// These static checks are not proof of runtime network behavior.
/// </summary>
public class OfflineAuditTests
{
    // System.Net.WebUtility is HTML/URL encoding; it performs no I/O.
    private static readonly HashSet<string> AllowedSystemNetTypes = new(StringComparer.Ordinal) { "System.Net.WebUtility" };

    private static readonly HashSet<string> ImageCacheAssemblies = new(StringComparer.Ordinal)
    { "System.Net.Http", "System.Net.Sockets", "System.Net.NameResolution", "System.Net.Primitives" };

    private static readonly HashSet<string> ImageCacheTypes = new(StringComparer.Ordinal)
    {
        "System.Net.Http.HttpClient", "System.Net.Http.HttpMessageHandler", "System.Net.Http.HttpMessageInvoker", "System.Net.Http.SocketsHttpHandler",
        "System.Net.Http.SocketsHttpConnectionContext", "System.Net.Http.HttpRequestMessage", "System.Net.Http.HttpMethod",
        "System.Net.Http.HttpResponseMessage", "System.Net.Http.HttpCompletionOption", "System.Net.Http.HttpContent",
        "System.Net.Http.HttpRequestException", "System.Net.Http.Headers.HttpRequestHeaders", "System.Net.Http.Headers.HttpResponseHeaders",
        "System.Net.Http.Headers.HttpContentHeaders", "System.Net.Http.Headers.HttpHeaderValueCollection`1",
        "System.Net.Http.Headers.MediaTypeWithQualityHeaderValue", "System.Net.Http.Headers.ProductInfoHeaderValue",
        // Used only by RemoteImageLoader to request fresh, non-stored image responses.
        "System.Net.Http.Headers.CacheControlHeaderValue", "System.Net.HttpStatusCode", "System.Net.IPAddress",
        // The reviewed handler explicitly assigns Credentials=null; the setter signature references this interface.
        "System.Net.ICredentials", "System.Net.Dns", "System.Net.DnsEndPoint", "System.Net.EndPoint", "System.Net.IPEndPoint",
        "System.Net.Sockets.Socket", "System.Net.Sockets.SocketType", "System.Net.Sockets.ProtocolType",
        "System.Net.Sockets.AddressFamily", "System.Net.Sockets.NetworkStream", "System.Net.Sockets.SocketException",
    };

    // Explicit consent, manually reviewed text only. No credential persistence, no
    // redirect/cookie/proxy handling, and no background connection attempts.
    private static readonly HashSet<string> AIConnectionTypes = new(StringComparer.Ordinal)
    {
        "System.Net.Http.HttpClient", "System.Net.Http.HttpClientHandler", "System.Net.Http.HttpMessageHandler",
        "System.Net.Http.HttpMessageInvoker", "System.Net.Http.HttpRequestMessage", "System.Net.Http.HttpMethod",
        "System.Net.Http.HttpResponseMessage", "System.Net.Http.HttpCompletionOption", "System.Net.Http.HttpContent",
        "System.Net.Http.StringContent", "System.Net.Http.Headers.AuthenticationHeaderValue",
        "System.Net.Http.Headers.HttpRequestHeaders", "System.Net.Http.Headers.HttpContentHeaders",
        "System.Net.Http.Headers.HttpHeaders",
        // Explicit model-list checks and sanitized failure metadata use these
        // readonly HTTP header types inside AIConnectionClient only.
        "System.Net.Http.Headers.HttpResponseHeaders", "System.Net.Http.Headers.RetryConditionHeaderValue",
        "System.Net.Http.Headers.HttpHeaderValueCollection`1", "System.Net.Http.Headers.MediaTypeWithQualityHeaderValue",
        "System.Net.HttpStatusCode", "System.Net.ICredentials",
    };

    // Explicit reviewed tool discovery/calls use only Streamable HTTP, without sockets,
    // redirects, OAuth or automatic retries. The service is a separate exact owner.
    private static readonly HashSet<string> MCPConnectionTypes = new(AIConnectionTypes, StringComparer.Ordinal)
    {
        "System.Net.Http.HttpRequestException",
        "System.Net.Http.Headers.MediaTypeHeaderValue",
        "System.Net.Http.Headers.HttpResponseHeaders",
        "System.Net.Http.Headers.HttpHeaderValueCollection`1",
        "System.Net.Http.Headers.MediaTypeWithQualityHeaderValue",
    };

    // Explicit Open-from-URL uses the reviewed public-address/pinned-socket
    // transport. These APIs remain forbidden to all other new owners.
    private static readonly HashSet<string> MarkdownUrlTypes = new(ImageCacheTypes, StringComparer.Ordinal)
    {
        "System.Net.Http.Headers.MediaTypeHeaderValue",
    };

    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "System.Net.Http", "System.Net.Sockets", "System.Net.WebClient", "System.Net.Requests", "System.Net.WebSockets",
        "System.Net.Mail", "System.Net.NameResolution", "System.Net.Ping", "System.Net.Security", "System.Net.NetworkInformation",
        "System.Net.ServicePoint", "System.Net.HttpListener", "System.Net.WebProxy", "System.Net.WebHeaderCollection",
        "Microsoft.AspNetCore", "Grpc", "RestSharp", "Flurl", "SignalR", "Microsoft.ApplicationInsights", "Sentry",
        "OpenTelemetry", "Microsoft.Extensions.Http", "Azure.", "AWSSDK", "Segment", "Mixpanel", "Amplitude", "AppCenter",
    ];

    private static readonly string[] ForbiddenTypeNamespaces =
    [
        "System.Net.Http", "System.Net.Sockets", "System.Net.Mail", "System.Net.NetworkInformation", "System.Net.Security",
        "System.Net.WebSockets", "System.Net.Cache", "System.Diagnostics.Tracing.EventSource",
    ];

    private static readonly string[] ForbiddenTypeNames =
    [
        "HttpClient", "HttpWebRequest", "WebRequest", "WebClient", "TcpClient", "UdpClient", "Socket", "Dns", "Ping",
        "SmtpClient", "ServicePointManager", "HttpListener", "WebProxy", "IPAddress", "IPEndPoint",
    ];

    private static readonly string[] TelemetryStrings =
    [
        "applicationinsights", "app center", "appcenter", "sentry.io", "google-analytics", "googletagmanager", "mixpanel",
        "segment.io", "amplitude", "datadog", "newrelic", "telemetry", "crashreport", "usage data", "phone home",
    ];

    // Reviewed: the About text states "no telemetry"; it is a promise, not a reference to a service.
    private static readonly string[] AllowedTelemetryStatements = ["Tern keeps your documents local."];

    public static IEnumerable<string> ShippedAssemblies()
    {
        var directory = AppContext.BaseDirectory;
        return Directory.EnumerateFiles(directory, "Tern*.dll")
            .Where(p => !Path.GetFileName(p).Contains("Tests", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> AllNetAssemblies()
    {
        var directory = AppContext.BaseDirectory;
        return Directory.EnumerateFiles(directory, "*.dll")
            .Where(p => !Path.GetFileName(p).Contains("Tests", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileName(p).StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileName(p).StartsWith("Microsoft.TestPlatform", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileName(p).StartsWith("Microsoft.VisualStudio", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileName(p).StartsWith("testhost", StringComparison.OrdinalIgnoreCase));
    }

    private static (List<string> Assemblies, List<string> Types, List<string> Strings) Inspect(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        var assemblies = reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name)).ToList();
        var types = new List<string>();
        foreach (var handle in reader.TypeReferences)
        {
            var reference = reader.GetTypeReference(handle);
            var ns = reader.GetString(reference.Namespace);
            var name = reader.GetString(reference.Name);
            types.Add(ns.Length == 0 ? name : ns + "." + name);
        }

        var strings = new List<string>();
        var heapSize = reader.GetHeapSize(HeapIndex.UserString);
        var handleValue = MetadataTokens.UserStringHandle(0);
        while (true)
        {
            handleValue = reader.GetNextHandle(handleValue);
            if (handleValue.IsNil)
            {
                break;
            }

            strings.Add(reader.GetUserString(handleValue));
        }

        _ = heapSize;
        return (assemblies, types, strings);
    }

    [Fact]
    public void The_shipped_assemblies_were_found()
    {
        var names = ShippedAssemblies().Select(Path.GetFileNameWithoutExtension).ToList();
        foreach (var expected in new[] { "Tern", "Tern.App", "Tern.Domain", "Tern.Export", "Tern.Filesystem", "Tern.Markdown", "Tern.PreviewView", "Tern.Recovery", "Tern.Rendering", "Tern.Search", "Tern.Workspaces" })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public void Networking_assembly_references_are_limited_to_reviewed_desktop_integrations()
    {
        var problems = new List<string>();
        foreach (var path in ShippedAssemblies())
        {
            var (assemblies, _, _) = Inspect(path);
            foreach (var reference in assemblies.Where(a => ForbiddenAssemblyPrefixes.Any(p => a.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
            {
                if (Path.GetFileNameWithoutExtension(path) == "Tern" && ImageCacheAssemblies.Contains(reference)) continue;
                problems.Add($"{Path.GetFileName(path)} -> {reference}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Networking_types_are_limited_to_the_reviewed_desktop_image_cache_allowlist()
    {
        var problems = new List<string>();
        foreach (var path in ShippedAssemblies())
        {
            var (_, types, _) = Inspect(path);
            foreach (var type in types.Distinct())
            {
                var lastDot = type.LastIndexOf('.');
                var ns = lastDot < 0 ? string.Empty : type[..lastDot];
                var name = lastDot < 0 ? type : type[(lastDot + 1)..];
                var isNet = ns.StartsWith("System.Net", StringComparison.Ordinal) && !AllowedSystemNetTypes.Contains(type);
                var forbiddenNamespace = ForbiddenTypeNamespaces.Any(f => type.StartsWith(f, StringComparison.Ordinal));
                var forbiddenName = ForbiddenTypeNames.Contains(name, StringComparer.Ordinal) && ns.StartsWith("System", StringComparison.Ordinal);
                if (isNet || forbiddenNamespace || forbiddenName)
                {
                    if (Path.GetFileNameWithoutExtension(path) == "Tern" && (ImageCacheTypes.Contains(type) || AIConnectionTypes.Contains(type) || MCPConnectionTypes.Contains(type))) continue;
                    problems.Add($"{Path.GetFileName(path)} uses {type}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void No_Tern_assembly_carries_telemetry_endpoints_or_vendor_names()
    {
        var problems = new List<string>();
        foreach (var path in ShippedAssemblies())
        {
            var (_, _, strings) = Inspect(path);
            foreach (var text in strings.Where(s => !AllowedTelemetryStatements.Any(a => s.StartsWith(a, StringComparison.Ordinal))))
            {
                foreach (var term in TelemetryStrings)
                {
                    if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"{Path.GetFileName(path)}: \"{(text.Length > 80 ? text[..80] : text)}\" matches '{term}'");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Network_looking_URLs_in_Tern_assemblies_are_only_XML_namespaces_or_documentation_text()
    {
        // Reviewed literals: XML namespace names, Avalonia resource URIs (avares:// resolves inside the assembly), the
        // http/https scheme names used to classify links, and regular expressions that match URL text in notes.
        // The image cache accepts document URL values, never a hardcoded service or telemetry endpoint.
        var allowedPrefixes = new[] { "http://www.w3.org/", "https://www.w3.org/", "avares://Tern/" };
        var allowedExact = new HashSet<string>(StringComparer.Ordinal) { "://", "http://", "https://" };
        // Explicit user-selected presets and their help text. Merely rendering them
        // never sends a request; exact strings keep unknown destinations reviewable.
        var reviewedConnectionText = new HashSet<string>(StringComparer.Ordinal)
        {
            // Exact static malformed-input hint. The example is displayed in an
            // ArgumentException, never submitted as a request. The URL dialog's
            // invalid-input regression separately verifies zero network calls.
            "Enter a complete HTTP or HTTPS Markdown URL, for example https://example.com/note.md.",
            // Static beginner-guide example only. URL import requires an explicit
            // Open action and never contacts this address just by showing help.
            "Example public GitHub file:\nhttps://github.com/gcfernando/developer-guides-and-tools/blob/main/docs/data-science/machine-learning-handbook.md\n\nA direct raw or download address also works.",
            // Reviewed static practice note. example.com is a documentation link;
            // displaying this text does not fetch it. Keep the entire literal exact.
            "# My practice note\n\nI can write **important words** and a [useful link](https://example.com).\n\n## Things to try\n\n- [ ] Change one sentence\n- [ ] Try Undo\n- [ ] Save this note\n\n> Small steps are enough.\n\n| Task | Status |\n| --- | --- |\n| Open a note | Done |\n| Save my work | Next |\n",
            "https://api.openai.com/v1/chat/completions",
            "https://api.openai.com/v1/models",
            "https://api.anthropic.com/v1/messages",
            "https://api.anthropic.com/v1/models",
            "http://localhost:11434/api/chat",
            "http://localhost:11434/api/tags",
            // Provider documentation opens only after its explicit link is clicked.
            "https://developers.openai.com/api/docs/quickstart",
            "https://platform.claude.com/docs/en/api/overview",
            "https://docs.ollama.com/quickstart",
            "https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/authenticate-copilot-cli",
            "https://modelcontextprotocol.io/specification/2025-11-25/basic/transports#streamable-http",
            "https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create",
            "http://localhost:3000/mcp",
            "Use https://api.anthropic.com/v1/messages, an Anthropic API key and an available Claude model ID. A Claude consumer subscription does not supply API credentials. Sends one reviewed text message; no tools or document access.",
            "Choose Claude · Anthropic API in the service presets. Confirm the full https://api.anthropic.com/v1/messages endpoint, enter the available model ID and API key, then review a short non-sensitive message before consent and Send.",
        };
        var problems = new List<string>();
        foreach (var path in ShippedAssemblies())
        {
            var (_, _, strings) = Inspect(path);
            foreach (var text in strings)
            {
                var index = text.IndexOf("://", StringComparison.Ordinal);
                // Reviewed editable example in AI Connections; no request occurs on opening.
                if (Path.GetFileNameWithoutExtension(path) == "Tern"
                    && text == "http://localhost:11434/v1/chat/completions") continue;
                if (Path.GetFileNameWithoutExtension(path) is "Tern" or "Tern.App" && reviewedConnectionText.Contains(text)) continue;
                if (index < 0 || allowedExact.Contains(text) || text.Contains("https?://", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!allowedPrefixes.Any(p => text.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add($"{Path.GetFileName(path)}: \"{(text.Length > 100 ? text[..100] : text)}\"");
                }
            }
        }

        Assert.True(problems.Count == 0, "Unreviewed URL literals:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Distinct()));
    }

    [Fact]
    public void Third_party_networking_assemblies_are_not_part_of_the_application_directory()
    {
        var present = AllNetAssemblies()
            .Select(Path.GetFileName)
            .Where(n => n is not null && ForbiddenAssemblyPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(present.Count == 0, string.Join(", ", present));
    }

    [Fact]
    public void Project_files_do_not_reference_networking_or_telemetry_packages()
    {
        var root = FindRepoRoot();
        var banned = new[] { "ApplicationInsights", "Sentry", "OpenTelemetry", "RestSharp", "Flurl", "Refit", "AppCenter", "Grpc", "SignalR", "Azure.", "AWSSDK", "Polly", "HttpClient" };
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(root, "Directory.Packages.props")))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            problems.AddRange(banned.Where(b => text.Contains(b, StringComparison.OrdinalIgnoreCase)).Select(b => $"{Path.GetRelativePath(root, file)} mentions {b}"));
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Network_API_source_usage_is_confined_to_reviewed_optional_integrations()
    {
        var root = FindRepoRoot();
        var banned = new[] { "HttpClient", "WebClient", "HttpWebRequest", "TcpClient", "UdpClient", "System.Net.Sockets", "System.Net.Http", "ServicePointManager", "Process.Start(\"http", "SocketsHttpHandler", "HttpRequestMessage", "Dns.", "new Socket(" };
        var problems = new List<string>();
        var reviewedFile = Path.GetFullPath(Path.Combine(root, "src", "Tern.Desktop", "Services", "RemoteImageLoader.cs"));
        var aiFile = Path.GetFullPath(Path.Combine(root, "src", "Tern.Desktop", "Services", "AIConnectionClient.cs"));
        var mcpFile = Path.GetFullPath(Path.Combine(root, "src", "Tern.Desktop", "Services", "MCPConnectionClient.cs"));
        var markdownUrlFile = Path.GetFullPath(Path.Combine(root, "src", "Tern.Desktop", "Services", "MarkdownUrlLoader.cs"));
        // Same reviewed MarkdownUrlLoader partial owner: bounded direct-asset requests reuse
        // its pinned public/explicit-loopback transport, redirect rules and explicit URL intent.
        var markdownBundleFile = Path.GetFullPath(Path.Combine(root, "src", "Tern.Desktop", "Services", "MarkdownUrlBundle.cs"));
        Assert.True(File.Exists(reviewedFile));
        Assert.True(File.Exists(aiFile));
        Assert.True(File.Exists(mcpFile));
        Assert.True(File.Exists(markdownUrlFile));
        Assert.True(File.Exists(markdownBundleFile));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            {
                continue;
            }
            if (Path.GetFullPath(file).Equals(reviewedFile, StringComparison.OrdinalIgnoreCase)
                || Path.GetFullPath(file).Equals(aiFile, StringComparison.OrdinalIgnoreCase)
                || Path.GetFullPath(file).Equals(mcpFile, StringComparison.OrdinalIgnoreCase)
                || Path.GetFullPath(file).Equals(markdownUrlFile, StringComparison.OrdinalIgnoreCase)
                || Path.GetFullPath(file).Equals(markdownBundleFile, StringComparison.OrdinalIgnoreCase)) continue;

            var text = File.ReadAllText(file);
            problems.AddRange(banned.Where(b => text.Contains(b, StringComparison.Ordinal)).Select(b => $"{Path.GetRelativePath(root, file)} mentions {b}"));
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Network_API_calls_and_constructors_cannot_move_outside_reviewed_owner_types()
    {
        var opcodes = typeof(OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
            .GroupBy(opcode => unchecked((ushort)opcode.Value)).ToDictionary(group => group.Key, group => group.First());
        var problems = new List<string>();
        foreach (var path in ShippedAssemblies())
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            foreach (var methodHandle in reader.MethodDefinitions)
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0) continue;
                var owner = method.GetDeclaringType();
                var outer = reader.GetTypeDefinition(owner);
                while (!outer.GetDeclaringType().IsNil) outer = reader.GetTypeDefinition(outer.GetDeclaringType());
                var reviewedOwner = Path.GetFileNameWithoutExtension(path) == "Tern"
                    && reader.GetString(outer.Namespace) == "Tern.Desktop.Services" && reader.GetString(outer.Name) == "RemoteImageLoader";
                var aiOwner = Path.GetFileNameWithoutExtension(path) == "Tern"
                    && reader.GetString(outer.Namespace) == "Tern.Desktop.Services" && reader.GetString(outer.Name) == "AIConnectionClient";
                var mcpOwner = Path.GetFileNameWithoutExtension(path) == "Tern"
                    && reader.GetString(outer.Namespace) == "Tern.Desktop.Services" && reader.GetString(outer.Name) == "MCPConnectionClient";
                var markdownUrlOwner = Path.GetFileNameWithoutExtension(path) == "Tern"
                    && reader.GetString(outer.Namespace) == "Tern.Desktop.Services" && reader.GetString(outer.Name) == "MarkdownUrlLoader";
                var bytes = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
                for (var position = 0; position < bytes.Length;)
                {
                    ushort code = bytes[position++];
                    if (code == 0xFE) code = (ushort)(0xFE00 | bytes[position++]);
                    var opcode = opcodes[code];
                    if (opcode.OperandType is OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineTok)
                    {
                        var token = MetadataTokens.EntityHandle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position, 4)));
                        var target = ReferencedType(reader, token);
                        if (target.StartsWith("System.Net", StringComparison.Ordinal) && !AllowedSystemNetTypes.Contains(target)
                            && !(reviewedOwner && ImageCacheTypes.Contains(target) || aiOwner && AIConnectionTypes.Contains(target) || mcpOwner && MCPConnectionTypes.Contains(target)
                                || markdownUrlOwner && MarkdownUrlTypes.Contains(target)))
                            problems.Add($"{Path.GetFileName(path)} {reader.GetString(reader.GetTypeDefinition(owner).Name)}.{reader.GetString(method.Name)} -> {target}");
                    }
                    position += opcode.OperandType switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        OperandType.InlineSwitch => checked(4 + 4 * BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position, 4))),
                        _ => 4,
                    };
                }
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static string ReferencedType(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.MethodSpecification) return ReferencedType(reader, reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
        if (handle.Kind == HandleKind.MemberReference) return ReferencedType(reader, reader.GetMemberReference((MemberReferenceHandle)handle).Parent);
        if (handle.Kind != HandleKind.TypeReference) return string.Empty;
        var type = reader.GetTypeReference((TypeReferenceHandle)handle);
        return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
    }

    [Fact]
    public void Third_party_assemblies_that_reference_networking_are_listed_for_review()
    {
        // Informational: Avalonia ships its own assemblies, some of which reference System.Net for features Tern never calls
        // (remote image loading, accessibility tooling). The checks above constrain Tern's explicit API surface; this
        // test only makes sure the audit can read every dependency without failing.
        var count = 0;
        foreach (var path in AllNetAssemblies())
        {
            try
            {
                Inspect(path);
                count++;
            }
            catch (BadImageFormatException)
            {
                // Native libraries (SQLite, SkiaSharp, HarfBuzz) are not managed assemblies.
            }
        }

        Assert.True(count > 10);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tern.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Tern.slnx not found above the test binaries.");
    }
}
