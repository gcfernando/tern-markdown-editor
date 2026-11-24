using System.Globalization;
using Tern.App.Documents;
using Tern.App.Help;
using Tern.Filesystem;
using Tern.Workspaces;

namespace Tern.App.Shell;

public sealed partial class ShellViewModel
{
    public string UserTemplatesDirectory => Path.Combine(Paths.SettingsDirectory, "templates");

    public async Task<StarterTemplateCatalog> LoadStarterTemplatesAsync()
    {
        var workspaceRoot = Workspace?.Root;
        var catalog = await Task.Run(() => StarterTemplates.Load(UserTemplatesDirectory, workspaceRoot)).ConfigureAwait(true);
        if (catalog.Warnings.Count > 0) SetStatus(string.Join(" ", catalog.Warnings), StatusSeverity.Warning);
        return catalog;
    }

    public DocumentTab NewDocumentFromTemplate(StarterTemplate template, DateTimeOffset? now = null)
    {
        var time = now ?? DateTimeOffset.Now;
        var suffix = " " + time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var nameLength = Math.Min(template.Name.Length, 255 - suffix.Length - ".md".Length);
        if (nameLength > 0 && char.IsHighSurrogate(template.Name[nameLength - 1])) --nameLength;
        var title = template.Name[..nameLength] + suffix;
        var tab = NewDocument(StarterTemplates.Expand(template.Markdown, title, time).ReplaceLineEndings(Settings.NewFileFormat.NewLineText), standalone: true);
        // Tools discover these files by exact name, so a date suffix would break them.
        tab.Session.SetDraftName(template.FileName ?? title + ".md");
        return tab;
    }

    public async Task<DocumentTab?> OpenTodaysNoteAsync(DateTimeOffset? now = null)
    {
        if (Workspace is not { IsNamedWorkspace: true } workspace)
        { SetStatus("Today's note needs an open workspace. Make this folder a workspace or open a workspace first.", StatusSeverity.Info); return null; }
        var time = now ?? DateTimeOffset.Now;
        var date = time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var journal = Path.Combine(workspace.Root, "Journal");
        var path = Path.Combine(journal, date + ".md");
        var template = StarterTemplates.All.Single(item => item.Name == "Daily Journal");
        var format = Settings.NewFileFormat;
        var text = StarterTemplates.Expand(template.Markdown, "Daily Journal " + date, time).ReplaceLineEndings(format.NewLineText);
        try
        {
            var result = await Task.Run(() =>
            {
                if (!Directory.Exists(journal))
                {
                    var folder = workspace.FileOperations.CreateFolder(workspace.Root, "Journal");
                    if (!folder.Succeeded && folder.Error != FileOperationError.AlreadyExists) return folder;
                }
                if (File.Exists(path) && CloudFiles.IsUnsafeReparsePoint(new FileInfo(path)))
                    return new FileOperationResult(false, null, null, FileOperationError.InvalidTarget, "Today's note is a linked file. Open it explicitly instead.");
                return workspace.FileOperations.CreateFile(journal, date + ".md", text, format: format);
            }).ConfigureAwait(true);
            if (!result.Succeeded && result.Error != FileOperationError.AlreadyExists)
            { SetStatus("Could not open today's note: " + result.Message, StatusSeverity.Error); return null; }
            if (_disposed || !OpenWorkspaces.Contains(workspace))
            {
                if (!_disposed) SetStatus("Today's note is at " + path + ", but its workspace closed before it could be opened.", StatusSeverity.Warning);
                return null;
            }
            var tab = await OpenFileAsync(path, createdFile: result.CreatedFile).ConfigureAwait(true);
            if (tab is not null)
                SetStatus((result.Succeeded ? "Created today's note: " : "Opened today's existing note: ") + path, StatusSeverity.Success);
            return tab;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetStatus("Could not open today's note: " + PlainProblem(ex), StatusSeverity.Error); return null; }
    }
}
