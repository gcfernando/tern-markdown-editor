using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Tern.Desktop.Services;
using Tern.Domain.Text;
using Tern.Markdown;

namespace Tern.Desktop.Views;

internal enum ImageImportChoice { Link, Copy, Cancel }

public partial class MainWindow
{
    private bool _repairingImage;

    private async Task RepairPreviewImageAsync(Tern.PreviewView.ImageActionEventArgs request)
    {
        var tab = _renderedPreviewTab;
        var revision = _renderedPreviewRevision;
        bool Current() => !_windowClosed && tab is not null && _shell.Tabs.Contains(tab) && ReferenceEquals(tab, _shown)
            && tab.Session.IsCurrent(revision) && ReferenceEquals(request.Document, Preview.Document);
        if (_repairingImage || !Current()) { _shell.StatusMessage = "The image preview changed. Wait for Reading to update, then try again."; return; }
        if (request.Action == Tern.PreviewView.ImageAction.Retry)
        {
            // The view retains its original approved loader; retry cannot change destination or policy.
            request.RetryApprovedLoad(); return;
        }
        if (request.Action == Tern.PreviewView.ImageAction.ReviewAccess)
        {
            var reason = request.Resolution is Tern.Rendering.Resources.Blocked blocked ? blocked.Detail : "Review this image's source location.";
            _dialogs?.ShowMessage("Image access", reason + "\nTern keeps its resource restrictions. Edit the source or explicitly choose a local image to use another location.");
            GoToLine(request.Line, 0); return;
        }
        if (tab is not { IsReadOnly: false, FilePath: not null })
        { _shell.StatusMessage = "Save a writable note before locating a replacement image."; return; }
        _repairingImage = true;
        try
        {
            var analysis = tab.Analysis ?? Tern.Markdown.MarkdownAnalyzer.Analyze(tab.CurrentText, tab.Profile);
            var matches = analysis.Links.Where(link => link.Kind == LinkKind.Image && link.Line == request.Line && link.Label == request.Alt).ToArray();
            if (matches.Length != 1) { GoToLine(request.Line, 0); _shell.StatusMessage = "Choose the exact image destination in Source to repair this reference."; return; }
            var link = matches[0];
            var options = new FilePickerOpenOptions { Title = "Locate replacement image", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Raster images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"] }] };
            if (ImageFilePicker is null && !StorageProvider.CanOpen) { _shell.StatusMessage = "The file picker is unavailable. Edit the image destination in Source."; return; }
            var files = await (ImageFilePicker?.Invoke(options) ?? StorageProvider.OpenFilePickerAsync(options));
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            if (!Current() || tab.IsReadOnly) { _shell.StatusMessage = "The note changed while locating the image. Your text was kept."; return; }
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (input.Length > ImageAttachmentImporter.MaxBytes) throw new IOException("Images must be at most 16 MiB.");
            using var bytes = new MemoryStream();
            var chunk = new byte[65536]; int count;
            while ((count = await input.ReadAsync(chunk)) > 0)
            { if (bytes.Length + count > ImageAttachmentImporter.MaxBytes) throw new IOException("Images must be at most 16 MiB."); bytes.Write(chunk, 0, count); }
            ImageAttachmentImporter.ValidateImage(bytes.ToArray(), Path.GetFileName(path));
            if (!Current() || tab.IsReadOnly) { _shell.StatusMessage = "The note changed while reading the image. Your text was kept."; return; }
            var selected = Tern.Filesystem.FileIdentity.ResolvePhysicalPath(path);
            var relative = Path.GetRelativePath(Path.GetDirectoryName(tab.FilePath)!, selected).Replace(Path.DirectorySeparatorChar, '/');
            var destination = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
            ImageAttachmentImporter.RegisterSelectedImage(tab, selected);
            tab.ApplyEdits([new TextEdit(link.Range.Start, link.Range.Length, destination)]);
            _shell.StatusMessage = "Image destination updated. Undo restores the old link; Save keeps the change.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { _shell.StatusMessage = "Could not locate image: " + ex.Message; }
        finally { _repairingImage = false; }
    }

    internal Func<FilePickerOpenOptions, Task<IReadOnlyList<IStorageFile>>>? ImageFilePicker { get; set; }
    internal Func<FilePickerSaveOptions, Task<IStorageFile?>>? ImageAttachmentPicker { get; set; }
    internal Func<string, Task<ImageImportChoice>>? ImageImportChoicePicker { get; set; }
    private static bool IsRasterImagePath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    private async Task InsertImageFromPickerAsync()
    {
        if (!await EnsureImageDocumentAsync()) return;
        var targetTab = _shown!;
        var targetRevision = targetTab.Session.Revision;
        try
        {
            IStorageItem[]? copiedFiles = null;
            try { if (Clipboard is not null) copiedFiles = await Clipboard.TryGetFilesAsync(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
            { _shell.StatusMessage = "The clipboard could not be read. Choose the image file instead."; }
            if (!ReferenceEquals(targetTab, _shown) || !targetTab.Session.IsCurrent(targetRevision))
            { _shell.StatusMessage = "The document changed while reading the clipboard. Insert the image again in the intended document."; return; }
            if (copiedFiles is { Length: > 0 })
            {
                var paths = copiedFiles.Select(file => file.TryGetLocalPath()).ToArray();
                if (paths.All(path => path is not null && IsRasterImagePath(path)))
                {
                    foreach (var copiedPath in paths)
                    {
                        if (!ReferenceEquals(targetTab, _shown))
                        { _shell.StatusMessage = "The active document changed. Insert the remaining images again."; break; }
                        if (!await ImportImageFileAsync(copiedPath!)) break;
                    }
                    return;
                }
            }
            if (ImageFilePicker is null && !StorageProvider.CanOpen)
            {
                _shell.StatusMessage = "The image file picker is unavailable. Copy an image file and try Insert Local Image again, or paste an image from the clipboard.";
                _dialogs?.ShowMessage("Image file picker unavailable", _shell.StatusMessage);
                return;
            }
            var options = new FilePickerOpenOptions
            {
                Title = "Choose a local image", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Raster images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"] }],
            };
            var files = await (ImageFilePicker?.Invoke(options) ?? StorageProvider.OpenFilePickerAsync(options));
            if (!ReferenceEquals(targetTab, _shown) || !targetTab.Session.IsCurrent(targetRevision))
            { _shell.StatusMessage = "The document changed while choosing the image. Insert it again in the intended document."; return; }
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await ImportImageFileAsync(path);
            else _shell.StatusMessage = "Image selection cancelled. Your document was kept.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            _shell.StatusMessage = "The image picker could not open: " + ex.Message;
            _dialogs?.ShowMessage("Could not choose an image", _shell.StatusMessage);
        }
    }

    private async Task<bool> ImportImageFileAsync(string path)
    {
        if (!await EnsureImageDocumentAsync()) return false;
        using var statusOperation = _shell.BeginStatusOperation();
        _shell.StatusMessage = "Reading image…";
        var tab = _shown!;
        var revision = tab.Session.Revision;
        var offset = Editor.CaretOffset;
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            if (input.Length > ImageAttachmentImporter.MaxBytes) throw new IOException("Images must be at most 16 MiB.");
            using var bytes = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await input.ReadAsync(buffer)) > 0)
            {
                if (bytes.Length + count > ImageAttachmentImporter.MaxBytes) throw new IOException("Images must be at most 16 MiB.");
                bytes.Write(buffer, 0, count);
            }
            if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision))
            { _shell.StatusMessage = "The document changed while reading the image. Insert it again."; return false; }
            var imageBytes = bytes.ToArray();
            ImageAttachmentImporter.ValidateImage(imageBytes, Path.GetFileName(path));
            if (ImageImportChoicePicker is null && ImageAttachmentPicker is null)
                return await ShowImageInsertionAsync(imageBytes, Path.GetFileName(path), path);
            var choice = ImageAttachmentImporter.IsBesideDocument(tab, path) ? ImageImportChoice.Link
                : ImageImportChoicePicker is { } choose ? await choose(path)
                : (await PromptWindow.AskAsync(this, "Add image",
                    "Link original uses the image at its current location. Copy image asks where to save a separate file.\n\n" + path,
                    ["Link original", "Copy image…", "Cancel"], 0, 2)) switch
                { 0 => ImageImportChoice.Link, 1 => ImageImportChoice.Copy, _ => ImageImportChoice.Cancel };
            if (choice == ImageImportChoice.Cancel)
            {
                _shell.StatusMessage = "Image insertion cancelled. Your document and source image were kept.";
                return false;
            }
            if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision))
            { _shell.StatusMessage = "The document changed while choosing how to add the image. Insert it again."; return false; }
            if (choice == ImageImportChoice.Link)
            {
                ImageAttachmentImporter.LinkExisting(tab, imageBytes, path, offset, revision);
                _shell.StatusMessage = "Original image linked. Save the document to keep the link. Moving the source image will break this link.";
                ReportLinkedImagePermissionFailure();
                return true;
            }
            return await InsertImageBytesAsync(imageBytes, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { _shell.StatusMessage = "Could not insert image: " + ex.Message; }
        return false;
    }

    private async Task PasteImageAsync()
    {
        if (!await EnsureImageDocumentAsync()) return;
        using var statusOperation = _shell.BeginStatusOperation();
        _shell.StatusMessage = "Reading clipboard image…";
        var tab = _shown!;
        var revision = tab.Session.Revision;
        try
        {
            if (Clipboard is null) { _shell.StatusMessage = "The clipboard is unavailable."; return; }
            var files = await Clipboard.TryGetFilesAsync();
            if (files is { Length: > 0 })
            {
                var paths = files.Select(file => file.TryGetLocalPath()).ToArray();
                if (paths.Any(path => path is null || !IsRasterImagePath(path)))
                { _shell.StatusMessage = "Copy a PNG, JPEG, GIF, WebP or BMP file in File Explorer, or copy image pixels, then try again."; return; }
                if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision))
                { _shell.StatusMessage = "The document changed while reading the clipboard. Paste again."; return; }
                foreach (var path in paths)
                {
                    if (!ReferenceEquals(tab, _shown)) break;
                    if (!await ImportImageFileAsync(path!)) break;
                }
                return;
            }
            using var bitmap = await Clipboard.TryGetBitmapAsync();
            if (bitmap is null)
            {
                _shell.StatusMessage = "No image was pasted. Copy a PNG, JPEG, GIF, WebP or BMP file in File Explorer, or copy image pixels, then use Paste Image as Attachment. Paste text with Ctrl+V.";
                return;
            }
            if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision))
            { _shell.StatusMessage = "The document changed while reading the clipboard. Paste again."; return; }
            if ((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height > ImageAttachmentImporter.MaxPixels)
            { _shell.StatusMessage = "The clipboard image exceeds 16 million pixels."; return; }
            using var bytes = new BoundedImageStream();
            await Task.Run(() => bitmap.Save(bytes, new Avalonia.Media.Imaging.PngBitmapEncoderOptions()));
            await InsertImageBytesAsync(bytes.ToArray(), "pasted-image.png");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        { _shell.StatusMessage = "Could not paste clipboard image: " + ex.Message; }
    }

    private async Task<bool> InsertImageBytesAsync(byte[] bytes, string suggestedName)
    {
        if (_shown is not { IsReadOnly: false, FilePath: not null } tab)
        { _shell.StatusMessage = "Save a writable document before inserting an image."; return false; }
        var revision = tab.Session.Revision;
        var offset = Editor.CaretOffset;
        var directory = Path.GetDirectoryName(tab.FilePath)!;
        if (ImageAttachmentPicker is null) return await ShowImageInsertionAsync(bytes, suggestedName, null);
        if (ImageAttachmentPicker is null && !StorageProvider.CanSave)
        {
            _shell.StatusMessage = "The attachment save picker is unavailable. Your document and existing images were kept.";
            _dialogs?.ShowMessage("Attachment picker unavailable", _shell.StatusMessage);
            return false;
        }
        try
        {
            var options = new FilePickerSaveOptions
            {
                Title = "Choose image copy folder and filename",
                SuggestedFileName = ImageAttachmentImporter.SuggestUniqueName(directory, suggestedName),
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(directory),
                DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.'),
                ShowOverwritePrompt = false,
            };
            var selected = await (ImageAttachmentPicker?.Invoke(options) ?? StorageProvider.SaveFilePickerAsync(options));
            if (selected?.TryGetLocalPath() is not { } target)
            { _shell.StatusMessage = "Image insertion cancelled. Your document was kept."; return false; }
            if (!ReferenceEquals(tab, _shown)) { _shell.StatusMessage = "The active document changed. Try inserting the image again."; return false; }
            await ImageAttachmentImporter.InsertAsync(tab, bytes, Path.GetDirectoryName(target)!, Path.GetFileName(target), offset, revision, allowExternalDestination: true, confirmReplace: ConfirmReplaceImageAsync);
            _shell.StatusMessage = "Image copied and linked. Save the document to keep the link. Undo removes the link; the attachment stays on disk.";
            ReportLinkedImagePermissionFailure();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        { _shell.StatusMessage = "Could not insert image: " + ex.Message; }
        return false;
    }

    private async Task<bool> ShowImageInsertionAsync(byte[] bytes, string name, string? original)
    {
        if (_shown is not { IsReadOnly: false, FilePath: not null } tab) return false;
        ImageAttachmentImporter.ValidateImage(bytes, name);
        var revision = tab.Session.Revision; var offset = Editor.CaretOffset; var source = tab.FilePath;
        bool Current() => !_windowClosed && _shell.Tabs.Contains(tab) && ReferenceEquals(tab, _shown) && tab.Session.IsCurrent(revision) && !tab.IsReadOnly && tab.FilePath == source;
        var result = await new InsertImageWindow(bytes, name, source, original, async choice =>
        {
            if (!Current()) return "The document changed while choosing the image. Close this form and insert it again.";
            if (choice.Copy)
                await ImageAttachmentImporter.InsertAsync(tab, bytes, choice.Folder, choice.Name, offset, revision, allowExternalDestination: true, altText: choice.Alt, isCurrent: Current, confirmReplace: ConfirmReplaceImageAsync);
            else if (original is not null)
                ImageAttachmentImporter.LinkExisting(tab, bytes, original, offset, revision, choice.Alt);
            return null;
        }).ShowDialog<ImageInsertion?>(this);
        if (result is null) return false;
        _shell.StatusMessage = result.Copy ? "Image copied and linked. Undo removes the link; the attachment stays on disk." : "Original image linked. Moving the original will break the link.";
        ReportLinkedImagePermissionFailure();
        return true;
    }

    private async Task<bool> ConfirmReplaceImageAsync(string target) =>
        await PromptWindow.AskAsync(OwnedWindows.OfType<InsertImageWindow>().FirstOrDefault(window => window.IsVisible) as Window ?? this, "Replace image file?",
            "Replace “" + Path.GetFileName(target) + "”?\n\nDestination: " + target +
            "\n\nOther notes may use this image. Cancel keeps the existing file and stops insertion.",
            ["Replace file", "Cancel"], 1, 1) == 0;

    private async Task<bool> EnsureImageDocumentAsync()
    {
        if (_shown is not { IsReadOnly: false } tab) return false;
        if (tab.FilePath is not null) return true;
        var confirmed = await PromptWindow.AskAsync(this, "Insert image",
            "Save this note before inserting an image\n\nSaving gives Tern a location for image attachments. After the note is saved, choose your image.",
            ["Save note", "Cancel"], 0, 1);
        if (confirmed != 0 || !ReferenceEquals(tab, _shown) || !_shell.Tabs.Contains(tab)) return false;
        _shell.StatusMessage = "Choose where to save the note; image insertion will continue after saving.";
        if (!await _shell.SaveTabAsync(tab))
        {
            _shell.StatusMessage = "The note was not saved, so no image was inserted. Your draft was kept.";
            return false;
        }
        if (!ReferenceEquals(tab, _shown) || !_shell.Tabs.Contains(tab) || tab.IsReadOnly || tab.FilePath is null)
        {
            _shell.StatusMessage = "The active document changed while saving. Insert the image again in the intended note.";
            return false;
        }
        return true;
    }

    private void ReportLinkedImagePermissionFailure()
    {
        if (_shell.LinkedImagePermissionError is { } error)
            _shell.StatusMessage += " Its selected-file permission could not be saved for reopening: " + error;
    }

    internal async Task PasteEditorAsync()
    {
        if (_shown is not { IsReadOnly: false } tab || Clipboard is null)
        { _shell.StatusMessage = "Choose a writable document before pasting."; return; }
        var revision = tab.Session.Revision;
        var start = Editor.SelectionStart; var length = Editor.SelectionLength;
        try
        {
            var files = await Clipboard.TryGetFilesAsync();
            if (files is { Length: > 0 })
            {
                var paths = files.Select(f => f.TryGetLocalPath()).ToArray();
                if (paths.Any(p => p is null || !IsRasterImagePath(p)))
                { _shell.StatusMessage = "Paste image files here. To open Markdown files or folders, use File → Open or drag them into Tern."; return; }
                if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision))
                { _shell.StatusMessage = "The document changed while reading the clipboard. Paste again."; return; }
                var problems = new List<string>();
                foreach (var path in paths)
                {
                    if (!ReferenceEquals(tab, _shown)) { problems.Add("The active document changed. Paste remaining images again."); break; }
                    if (!await ImportImageFileAsync(path!)) problems.Add(Path.GetFileName(path) + ": " + _shell.StatusMessage);
                }
                if (problems.Count > 0) _shell.StatusMessage = string.Join(" · ", problems);
                return;
            }
            var text = await Clipboard.TryGetTextAsync();
            if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision))
            { _shell.StatusMessage = "The document changed while reading the clipboard. Paste again."; return; }
            if (text is not null)
            {
                var link = Formatting.PasteUrlOverSelection(tab.CurrentText, start, length, text);
                ApplyEdit(link ?? new EditResult([new TextEdit(start, length, text)], start + text.Length, 0));
                return;
            }
            await PasteImageAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        { _shell.StatusMessage = "Nothing was pasted. Copy text or a PNG, JPEG, GIF, WebP or BMP image and try again. Details: " + ex.Message; }
    }
}

/// <summary>Limits clipboard encoding before the encoder can grow its output beyond the attachment budget.</summary>
internal sealed class BoundedImageStream(int maxBytes = ImageAttachmentImporter.MaxBytes) : MemoryStream
{
    private void CheckSize(long size)
    {
        if (size > maxBytes) throw new IOException("Encoded images must be at most 16 MiB.");
    }
    public override void Write(byte[] buffer, int offset, int count) { CheckSize(Position + count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { CheckSize(Position + buffer.Length); base.Write(buffer); }
    public override void WriteByte(byte value) { CheckSize(Position + 1); base.WriteByte(value); }
    public override void SetLength(long value) { CheckSize(value); base.SetLength(value); }
}
