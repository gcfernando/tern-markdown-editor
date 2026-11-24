using SkiaSharp;
using Tern.App.Documents;
using Tern.App.Shell;
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

    public static void LinkExisting(DocumentTab tab, byte[] bytes, string path, int offset, long revision, string? altText = null)
    {
        if (tab.IsReadOnly || tab.FilePath is not { } document) throw new IOException("Save a writable document before inserting an attachment.");
        var source = FileIdentity.ResolvePhysicalPath(path);
        ValidateImage(bytes, Path.GetFileName(source));
        if (!tab.Session.IsCurrent(revision)) throw new IOException("The document changed while reading the image. Try again.");
        RegisterSelectedImage(tab, source);
        InsertLink(tab, document, source, Path.GetFileName(path), offset, altText);
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

    /// <summary>Confirmation text for replacing an existing attachment; lists the notes that already show it.</summary>
    internal static string BuildReplaceMessage(string target, IReadOnlyList<string> referencingNotes)
    {
        const int shown = 8;
        var text = "Replace “" + Path.GetFileName(target) + "”?\n\nDestination: " + target
            + "\n\nThe old image goes to the Windows Recycle Bin, so it can be restored from there.";
        if (referencingNotes.Count == 0)
            return text + "\n\nNo other note in the Library links to this image. Cancel keeps the existing file and stops insertion.";
        return text + $"\n\n{referencingNotes.Count} other note(s) use this image and will show the new one:\n"
            + string.Join("\n", referencingNotes.Take(shown).Select(note => "• " + note))
            + (referencingNotes.Count > shown ? $"\n…and {referencingNotes.Count - shown} more." : string.Empty)
            + "\n\nCancel keeps the existing file and stops insertion.";
    }

    public static async Task<string> InsertAsync(DocumentTab tab, byte[] bytes, string folder, string name, int offset, long revision, bool allowExternalDestination = false, string? altText = null, Func<bool>? isCurrent = null, Func<string, Task<bool>>? confirmReplace = null, ILocalFileDeletion? recycleBin = null)
    {
        if (tab.IsReadOnly || tab.FilePath is not { } document) throw new IOException("Save a writable document before inserting an attachment.");
        ValidateImage(bytes, name);
        if (isCurrent?.Invoke() == false || !tab.Session.IsCurrent(revision)) throw new IOException("The intended document changed. Insert the image again.");
        var targetFolder = FileIdentity.ResolvePhysicalPath(folder);
        var root = FileIdentity.ResolvePhysicalPath(Path.GetDirectoryName(document)!);
        if (!allowExternalDestination && !WorkspacePaths.IsSameOrUnder(root, targetFolder))
            throw new IOException("Choose an attachment folder inside the document's folder.");
        Directory.CreateDirectory(targetFolder);
        var target = Path.Combine(targetFolder, name);
        if (Directory.Exists(target)) throw new IOException("That destination is a folder. Choose another filename.");
        byte[]? previousHash = null;
        if (File.Exists(target))
        {
            if (confirmReplace is null) throw new IOException("That filename already exists. Choose another name; the existing attachment was kept.");
            if (recycleBin is null) throw new IOException("The existing attachment can only be replaced when it can be moved to the Recycle Bin. Choose another name; the existing file was kept.");
            if (CloudFiles.IsUnsafeReparsePoint(new FileInfo(target))) throw new IOException("Choose a destination that is not a linked file.");
            using (var previous = File.OpenRead(target))
            {
                if (previous.Length > MaxBytes) throw new IOException("The existing destination exceeds 16 MiB. Choose another filename; the existing file was kept.");
                previousHash = await System.Security.Cryptography.SHA256.HashDataAsync(previous);
            }
            if (!await confirmReplace(target)) throw new IOException("Image replacement cancelled. The existing file was kept.");
        }
        var staged = Path.Combine(targetFolder, ".tern-image-" + Guid.NewGuid().ToString("N") + ".tmp");
        var committed = false;
        try
        {
            await File.WriteAllBytesAsync(staged, bytes);
            if (isCurrent?.Invoke() == false || !tab.Session.IsCurrent(revision) || tab.FilePath != document || tab.IsReadOnly) throw new IOException("The document changed while choosing the attachment. Try again.");
            // Re-resolve the directory before committing so a changed link cannot redirect the copy.
            if (FileIdentity.ResolvePhysicalPath(folder) != targetFolder) throw new IOException("The attachment folder changed. Try again.");
            if (previousHash is not null)
            {
                if (CloudFiles.IsUnsafeReparsePoint(new FileInfo(target))) throw new IOException("The destination changed to a linked file after confirmation. Insert the image again.");
                using (var previous = File.OpenRead(target))
                    if (!(await System.Security.Cryptography.SHA256.HashDataAsync(previous)).SequenceEqual(previousHash))
                        throw new IOException("The destination changed after confirmation. Insert the image again.");
                try { await recycleBin!.MoveToRecycleBinAsync(target); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or NotSupportedException or System.ComponentModel.Win32Exception)
                { throw new IOException("The existing image could not be moved to the Recycle Bin, so it was kept: " + ex.Message, ex); }
                if (File.Exists(target)) throw new IOException("The existing image is still in place after the Recycle Bin request, so it was kept.");
            }
            try { File.Move(staged, target, overwrite: false); }
            catch (Exception ex) when (previousHash is not null && ex is IOException or UnauthorizedAccessException)
            { throw new IOException($"The old image was moved to the Recycle Bin but the new one could not be written to '{target}'. Restore the old image from the Recycle Bin: " + ex.Message, ex); }
            committed = true;
            RegisterSelectedImage(tab, target);
            InsertLink(tab, document, target, name, offset, altText);
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

    private static void InsertLink(DocumentTab tab, string document, string target, string name, int offset, string? altText)
    {
        var relative = Path.GetRelativePath(Path.GetDirectoryName(document)!, target).Replace(Path.DirectorySeparatorChar, '/');
        var escaped = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        var alt = (altText ?? Path.GetFileNameWithoutExtension(name)).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        tab.ApplyEdits([TextEdit.Insert(offset, $"![{alt}]({escaped})")]);
    }

    internal static void RegisterSelectedImage(DocumentTab tab, string path)
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
