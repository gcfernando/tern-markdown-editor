using System.Globalization;
using Tern.Filesystem;
using Tern.Workspaces;

namespace Tern.App.Help;

public sealed record StarterTemplateCatalog(IReadOnlyList<StarterTemplate> Items, IReadOnlyList<string> Warnings);

public static partial class StarterTemplates
{
    public const int MaximumTemplateBytes = 1024 * 1024;
    public const int MaximumTemplatesPerFolder = 100;

    public static string Expand(string markdown, string title, DateTimeOffset now) =>
        markdown.Replace("{{date}}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("YYYY-MM-DD", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{time}}", now.ToString("HH:mm", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{title}}", title, StringComparison.Ordinal).ReplaceLineEndings("\r\n");

    public static StarterTemplateCatalog Load(string userDirectory, string? workspaceRoot = null)
    {
        var templates = All.ToList();
        var warnings = new List<string>();
        var buffer = new byte[MaximumTemplateBytes + 1];
        void LoadFolder(string directory, string root, string scope)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    if (File.Exists(directory)) warnings.Add("The template folder is a file: " + directory);
                    return;
                }
                var folder = new DirectoryInfo(directory);
                if (!WorkspacePaths.IsPhysicallyContained(root, directory) || CloudFiles.IsUnsafeReparsePoint(folder)
                    || CloudFiles.IsOnlineOnly(folder.Attributes))
                { warnings.Add("Skipped a linked or online-only template folder: " + directory); return; }
                var files = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Take(MaximumTemplatesPerFolder + 1).ToArray();
                if (files.Length > MaximumTemplatesPerFolder)
                    warnings.Add($"Only the first {MaximumTemplatesPerFolder} templates were loaded from {directory}.");
                foreach (var path in files.Take(MaximumTemplatesPerFolder))
                {
                    try
                    {
                        var file = new FileInfo(path);
                        if (CloudFiles.IsUnsafeReparsePoint(file) || CloudFiles.IsOnlineOnly(file.Attributes))
                        { warnings.Add("Skipped a linked or online-only template: " + path); continue; }
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                        if (count > MaximumTemplateBytes)
                        { warnings.Add("Template exceeds the 1 MiB limit: " + path); continue; }
                        var decoded = TextFileCodec.Decode(buffer.AsSpan(0, count));
                        if (!decoded.IsLossless)
                        { warnings.Add("Template encoding could not be decoded losslessly: " + path); continue; }
                        templates.Add(new StarterTemplate(Path.GetFileNameWithoutExtension(path), scope + " template: " + path, decoded.Text, path));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { warnings.Add("Could not read template " + path + ": " + ex.Message); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add("Could not read template folder " + directory + ": " + ex.Message); }
        }
        var fullUserDirectory = Path.GetFullPath(userDirectory);
        LoadFolder(fullUserDirectory, Path.GetDirectoryName(fullUserDirectory) ?? fullUserDirectory, "User");
        if (workspaceRoot is not null)
            LoadFolder(Path.Combine(workspaceRoot, ".tern", "templates"), workspaceRoot, "Workspace");
        return new StarterTemplateCatalog(templates, warnings);
    }
}
