using SkiaSharp;
using Tern.App.Documents;
using Tern.Domain.Text;
using Tern.Filesystem;
using Tern.Workspaces;

namespace Tern.Desktop.Services;

/// <summary>Stages a bounded raster attachment and then inserts one undoable relative Markdown link.</summary>
internal static class ImageAttachmentImporter
{
    internal const int MaxBytes = 16 * 1024 * 1024;
    internal const long MaxPixels = 16_000_000;

    public static bool IsBesideDocument(DocumentTab tab, string path) => tab.FilePath is { } document
        && WorkspacePaths.PathsEqual(FileIdentity.ResolvePhysicalPath(Path.GetDirectoryName(document)!),
            Path.GetDirectoryName(FileIdentity.ResolvePhysicalPath(path))!);

    public static void LinkExisting(DocumentTab tab, byte[] bytes, string path, int offset, long revision)
    {
        if (tab.IsReadOnly || tab.FilePath is not { } document) throw new IOException("Save a writable document before inserting an attachment.");
        var source = FileIdentity.ResolvePhysicalPath(path);
        ValidateImage(bytes, Path.GetFileName(source));
        if (!tab.Session.IsCurrent(revision)) throw new IOException("The document changed while reading the image. Try again.");
        RegisterSelectedImage(tab, source);
        InsertLink(tab, document, source, Path.GetFileName(path), offset);
    }

    public static string SuggestUniqueName(string folder, string name)
    {
        if (!File.Exists(Path.Combine(folder, name)) && !Directory.Exists(Path.Combine(folder, name))) return name;
        var stem = Path.GetFileNameWithoutExtension(name); var extension = Path.GetExtension(name);
        for (var number = 2; number < 10000; number++)
        {
            var candidate = $"{stem}-{number}{extension}";
            if (!File.Exists(Path.Combine(folder, candidate)) && !Directory.Exists(Path.Combine(folder, candidate))) return candidate;
        }
        throw new IOException("Choose a different attachment name; too many copies use this name.");
    }

    public static async Task<string> InsertAsync(DocumentTab tab, byte[] bytes, string folder, string name, int offset, long revision, bool allowExternalDestination = false)
    {
        if (tab.IsReadOnly || tab.FilePath is not { } document) throw new IOException("Save a writable document before inserting an attachment.");
        ValidateImage(bytes, name);
        var targetFolder = FileIdentity.ResolvePhysicalPath(folder);
        var root = FileIdentity.ResolvePhysicalPath(Path.GetDirectoryName(document)!);
        if (!allowExternalDestination && !WorkspacePaths.IsSameOrUnder(root, targetFolder))
            throw new IOException("Choose an attachment folder inside the document's folder.");
        Directory.CreateDirectory(targetFolder);
        var target = Path.Combine(targetFolder, name);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException("That filename already exists. Choose another name; the existing attachment was kept.");
        var staged = Path.Combine(targetFolder, ".tern-image-" + Guid.NewGuid().ToString("N") + ".tmp");
        var committed = false;
        try
        {
            await File.WriteAllBytesAsync(staged, bytes);
            if (!tab.Session.IsCurrent(revision) || tab.FilePath != document || tab.IsReadOnly) throw new IOException("The document changed while choosing the attachment. Try again.");
            // Re-resolve the directory before committing so a changed link cannot redirect the copy.
            if (FileIdentity.ResolvePhysicalPath(folder) != targetFolder) throw new IOException("The attachment folder changed. Try again.");
            File.Move(staged, target, overwrite: false);
            committed = true;
            RegisterSelectedImage(tab, target);
            InsertLink(tab, document, target, name, offset);
            return target;
        }
        catch (Exception ex) when (committed)
        {
            // Another writer may already have replaced this path. Without a held
            // filesystem identity, deleting it could discard someone else's bytes.
            throw new IOException($"The attachment was retained at '{target}' because its link could not be inserted.", ex);
        }
        finally { File.Delete(staged); }
    }

    private static void InsertLink(DocumentTab tab, string document, string target, string name, int offset)
    {
        var relative = Path.GetRelativePath(Path.GetDirectoryName(document)!, target).Replace(Path.DirectorySeparatorChar, '/');
        var escaped = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        var alt = Path.GetFileNameWithoutExtension(name).Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
        tab.ApplyEdits([TextEdit.Insert(offset, $"![{alt}]({escaped})")]);
    }

    private static void RegisterSelectedImage(DocumentTab tab, string path)
    {
        var root = Path.GetDirectoryName(tab.FilePath!)!;
        if (WorkspacePaths.IsSameOrUnder(root, path)) return;
        if (tab.LinkedImageFiles.Contains(path, WorkspacePaths.Comparer)) return;
        if (tab.LinkedImageFiles.Count >= LinkedImageStore.MaxImages)
            throw new IOException("This note already links 128 images outside its folder. Copy further images inside the note folder.");
        tab.LinkedImageFiles = [.. tab.LinkedImageFiles, path];
    }

    internal static void ValidateImage(byte[] bytes, string name)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new IOException("Images must be at most 16 MiB.");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp")) throw new IOException("Choose a PNG, JPEG, GIF, WebP or BMP image.");
        if (!FileNameValidator.Validate(name).IsValid) throw new IOException("Choose a safe attachment filename.");
        using var imageStream = new SKMemoryStream(bytes);
        using (var codec = SKCodec.Create(imageStream))
        {
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > MaxPixels)
                throw new IOException("The image is invalid or exceeds 16 million pixels.");
            var actual = codec.EncodedFormat switch { SKEncodedImageFormat.Png => ".png", SKEncodedImageFormat.Jpeg => ".jpg", SKEncodedImageFormat.Gif => ".gif", SKEncodedImageFormat.Webp => ".webp", SKEncodedImageFormat.Bmp => ".bmp", _ => string.Empty };
            if (actual != extension && !(actual == ".jpg" && extension == ".jpeg")) throw new IOException("Image contents do not match the filename extension.");
        }
    }
}
