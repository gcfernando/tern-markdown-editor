using Tern.Filesystem;
using Tern.Workspaces;

namespace Tern.App.Shell;

public sealed record KnowledgeSourceState(string PhysicalPath, FileContentStamp? Disk, long OverlayRevision, Guid SessionId);

public sealed partial class WorkspaceSession
{
    private readonly Guid _knowledgeStateSession = Guid.NewGuid();
    /// <summary>Fresh authorization and file/overlay identity, without reading content. Null Disk requires a digest check.</summary>
    public KnowledgeSourceState CaptureKnowledgeState(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var full = WorkspacePaths.Normalize(path);
        var filter = new WorkspaceFilter(Root, Options, requireReadableIgnoreFile: true);
        if (!WorkspacePaths.IsUnder(Root, full) || !filter.IsDocument(full) || !filter.IsRelevantChange(full) || !IsKnownFile(full)
            || !WorkspacePaths.IsPhysicallyContained(Root, full)) throw new InvalidOperationException("The source is no longer authorized in this workspace. Refresh your notes.");
        if (!File.Exists(full) || CloudFiles.IsOnlineOnly(full)) throw new IOException("A source note is missing or unavailable. Refresh your notes.");
        for (var directory = Path.GetDirectoryName(full); directory is not null && WorkspacePaths.IsUnder(Root, directory); directory = Path.GetDirectoryName(directory))
            if (filter.IsDirectoryExcluded(Path.GetFileName(directory), WorkspacePaths.GetRelative(Root, directory), File.GetAttributes(directory)))
                throw new InvalidOperationException("A source note is now excluded. Refresh your notes.");
        return new(FileIdentity.ResolvePhysicalPath(full), FileIdentity.TryGetContentStamp(full), _textVersions.GetValueOrDefault(full), _knowledgeStateSession);
    }
}
