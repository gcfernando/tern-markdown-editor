using Tern.Markdown;
using Tern.Rendering.Resources;
using Tern.Workspaces;

namespace Tern.App.Documents;

internal static class LinkedImageAccess
{
    public static bool IsAvailable(string document, LinkInfo link, IReadOnlyList<string> selectedFiles)
    {
        if (link.Kind != LinkKind.Image || selectedFiles.Count == 0) return false;
        var resolver = new SafeResourceResolver(new ResourceResolverOptions
        { DocumentDirectory = Path.GetDirectoryName(document), AdditionalImageFiles = selectedFiles });
        return resolver.ResolveImage(link.Destination) is LocalFile { Exists: true } image
            && selectedFiles.Contains(image.CanonicalPath, WorkspacePaths.Comparer);
    }
}
