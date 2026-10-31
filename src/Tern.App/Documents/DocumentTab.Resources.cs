using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Tern.Filesystem;
using Tern.Markdown;
using Tern.Workspaces;

namespace Tern.App.Documents;

public sealed partial class DocumentTab
{
    /// <summary>Resource base for an imported or recovered draft; never its saved-file identity.</summary>
    public string? ResourceDocumentPath { get; set; }
    public IReadOnlyDictionary<string, byte[]> ResourceFiles { get; set; } = new Dictionary<string, byte[]>();

    private async Task<List<string>> CopyResourcesAsync(string destination, string text, CancellationToken token)
    {
        const long maxFileBytes = 64 * 1024 * 1024;
        const long maxBundleBytes = 256 * 1024 * 1024;
        const int maxFiles = 512;
        var created = new List<string>();
        var sourceDocument = FilePath ?? ResourceDocumentPath;
        var sourceRoot = sourceDocument is null ? null : Path.GetDirectoryName(Path.GetFullPath(sourceDocument))!;
        var destinationRoot = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        // An ordinary save must never restore attachments over the user's edited files.
        if (sourceRoot is not null && WorkspacePaths.PathsEqual(sourceRoot, destinationRoot)) return created;

        var pending = new Queue<string>();
        var seen = new HashSet<string>(WorkspacePaths.Comparer);
        long totalBytes = 0;

        void Collect(string markdown, string relativeDirectory)
        {
            foreach (var link in MarkdownAnalyzer.Analyze(markdown, Profile).Links)
            {
                var parsed = LinkTarget.Parse(link.Destination, link.Kind == LinkKind.WikiLink);
                if (parsed.Kind is not (LinkTargetKind.Relative or LinkTargetKind.WikiLink)) continue;
                // An already-invalid reference must not prevent saving the note itself.
                if (parsed.Path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
                var relative = Path.GetRelativePath(destinationRoot, Path.GetFullPath(Path.Combine(destinationRoot, relativeDirectory, parsed.Path)));
                if (sourceRoot is not null && link.Kind == LinkKind.WikiLink
                    && !File.Exists(Path.Combine(sourceRoot, relative)) && Path.GetExtension(relative).Length == 0)
                    relative += ".md";
                var source = sourceRoot is null ? null : Path.GetFullPath(Path.Combine(sourceRoot, relative));
                // Never copy through ../ outside the chosen destination.
                if (!WorkspacePaths.IsSameOrUnder(destinationRoot, Path.Combine(destinationRoot, relative)))
                {
                    if (source is not null && (File.Exists(source) || Directory.Exists(source)))
                        throw new IOException("A relative reference leaves the document folder: " + link.Destination + ". Keep this copy in the original folder, or move that attachment inside the document folder first.");
                    continue;
                }
                if ((source is not null && File.Exists(source)) || ResourceFiles.ContainsKey(relative.Replace('\\', '/')))
                    pending.Enqueue(relative);
            }
        }

        try
        {
            foreach (var relative in ResourceFiles.Keys)
            {
                if (!Regex.IsMatch(relative, @"^assets/[a-f0-9]{64}(?:\.[a-z0-9]{1,11})?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
                    throw new IOException("The downloaded resource path is unsafe.");
                pending.Enqueue(relative);
            }
            Collect(text, string.Empty);
            while (pending.TryDequeue(out var relative))
            {
                token.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(Path.Combine(destinationRoot, relative));
                if (!seen.Add(target)) continue;
                if (seen.Count > maxFiles) throw new IOException("The attachment bundle exceeds 512 files. Keep this copy in the original folder.");
                if (WorkspacePaths.PathsEqual(target, destination))
                    throw new IOException("The document filename conflicts with a referenced attachment. Choose another filename.");
                if (!WorkspacePaths.IsSameOrUnder(destinationRoot, target)) throw new IOException("The attachment path leaves the destination folder.");
                for (FileSystemInfo? entry = new FileInfo(target); entry is not null; entry = entry is DirectoryInfo directory ? directory.Parent : ((FileInfo)entry).Directory)
                    if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Attachments cannot be saved through a linked directory or file.");

                var source = sourceRoot is null ? null : Path.GetFullPath(Path.Combine(sourceRoot, relative));
                byte[] bytes;
                if (source is not null && File.Exists(source))
                {
                    if (!WorkspacePaths.IsPhysicallyContained(sourceRoot!, source)) throw new IOException("A linked attachment leaves the document folder.");
                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    if (input.Length > maxFileBytes || totalBytes + input.Length > maxBundleBytes) throw new IOException("The attachment bundle exceeds its size limit (64 MiB per file, 256 MiB total).");
                    using var buffer = new MemoryStream();
                    var chunk = new byte[65536];
                    int count;
                    while ((count = await input.ReadAsync(chunk, token).ConfigureAwait(true)) > 0)
                    {
                        if (buffer.Length + count > maxFileBytes || totalBytes + buffer.Length + count > maxBundleBytes)
                            throw new IOException("The attachment bundle exceeds its size limit (64 MiB per file, 256 MiB total).");
                        buffer.Write(chunk, 0, count);
                    }
                    bytes = buffer.ToArray();
                }
                else if (!ResourceFiles.TryGetValue(relative.Replace('\\', '/'), out bytes!))
                    throw new IOException("An attachment became unavailable: " + relative);

                totalBytes += bytes.Length;
                if (bytes.LongLength > maxFileBytes || totalBytes > maxBundleBytes) throw new IOException("The attachment bundle exceeds its size limit (64 MiB per file, 256 MiB total).");
                if (source is not null && Path.GetExtension(source).ToLowerInvariant() is ".md" or ".markdown" or ".mdown")
                {
                    var decoded = TextFileCodec.Decode(bytes);
                    if (decoded.IsLossless) Collect(decoded.Text, Path.GetDirectoryName(relative) ?? string.Empty);
                }
                if (File.Exists(target))
                {
                    await using var existing = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    if (existing.Length != bytes.LongLength || !(await SHA256.HashDataAsync(existing, token).ConfigureAwait(true)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                        throw new IOException("A different attachment already exists at " + target + ". Choose another destination folder.");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                created.Add(target);
                await output.WriteAsync(bytes, token).ConfigureAwait(true);
            }
            return created;
        }
        catch { RemoveCreatedResources(created); throw; }
    }

    private static void RemoveCreatedResources(List<string> paths)
    {
        foreach (var path in paths)
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Keep the original save failure. */ }
    }
}
