using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Tern.App.Editing;
using Tern.App.Shell;
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
        using var operation = _shell.BeginStatusOperation("Repairing preview image link", tab.DisplayName);
        try
        {
            operation.Report("Finding the exact image reference in the current Markdown");
            var analysis = tab.Analysis ?? Tern.Markdown.MarkdownAnalyzer.Analyze(tab.CurrentText, tab.Profile);
            var matches = analysis.Links.Where(link => link.Kind == LinkKind.Image && link.Line == request.Line && link.Label == request.Alt).ToArray();
            if (matches.Length != 1) { GoToLine(request.Line, 0); _shell.StatusMessage = "Choose the exact image destination in Source to repair this reference."; return; }
            var link = matches[0];
            var options = new FilePickerOpenOptions
            {
                Title = "Locate replacement image",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Raster images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"] }]
            };
            if (ImageFilePicker is null && !StorageProvider.CanOpen) { _shell.StatusMessage = "The file picker is unavailable. Edit the image destination in Source."; return; }
            operation.WaitForUser("Choose the replacement image; document unchanged");
            var files = await (ImageFilePicker?.Invoke(options) ?? StorageProvider.OpenFilePickerAsync(options));
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            if (!Current() || tab.IsReadOnly) { _shell.StatusMessage = "The note changed while locating the image. Your text was kept."; return; }
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            operation.Report("Reading and validating the replacement image");
            if (input.Length > ImageAttachmentImporter.MaxBytes) throw new IOException("Images must be at most 16 MiB.");
            using var bytes = new MemoryStream();
            var chunk = new byte[65536]; int count;
            while ((count = await input.ReadAsync(chunk)) > 0)
            { if (bytes.Length + count > ImageAttachmentImporter.MaxBytes) throw new IOException("Images must be at most 16 MiB."); bytes.Write(chunk, 0, count); operation.Report("Reading the replacement image", bytes.Length, input.Length, "bytes"); }
            ImageAttachmentImporter.ValidateImage(bytes.ToArray(), Path.GetFileName(path));
            if (!Current() || tab.IsReadOnly) { _shell.StatusMessage = "The note changed while reading the image. Your text was kept."; return; }
            var selected = Tern.Filesystem.FileIdentity.ResolvePhysicalPath(path);
            if (InsertLinkWindow.RelativeDestination(tab.FilePath!, selected) is not { } destination)
            { _shell.SetStatus("This image is on a different drive, so the link would not work. Copy it to the same drive as this note. Your text was kept.", Tern.App.Shell.StatusSeverity.Warning); return; }
            operation.Report("Applying the approved image destination as an undoable buffer edit");
            ImageAttachmentImporter.RegisterSelectedImage(tab, selected);
            tab.ApplyEdits([new TextEdit(link.Range.Start, link.Range.Length, destination)]);
            _shell.StatusMessage = "Image destination updated. Undo restores the old link; Save keeps the change.";
            ReportLinkedImagePermissionFailure();
            operation.Complete(_shell.LinkedImagePermissionError, _shell.LinkedImagePermissionError is not null,
                "Image destination updated in buffer; not saved; Undo available");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { operation.Fail(ex.Message); _shell.StatusMessage = "Could not locate image: " + Tern.App.Shell.UserProblem.Plain(ex); }
        finally { operation.Cancel(); _repairingImage = false; }
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
                        { _shell.SetStatus("The active document changed. Insert the remaining images again.", Tern.App.Shell.StatusSeverity.Warning); break; }
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
                Title = "Choose a local image",
                AllowMultiple = false,
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
            _shell.StatusMessage = "The image picker could not open: " + _shell.PlainProblem(ex);
            _dialogs?.ShowMessage("Could not choose an image", _shell.StatusMessage);
        }
    }

    private async Task<bool> ImportImageFileAsync(string path)
    {
        if (!await EnsureImageDocumentAsync()) return false;
        using var statusOperation = _shell.BeginStatusOperation("Importing local image", Path.GetFileName(path));
        statusOperation.Report("Reading the selected raster image");
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
                statusOperation.Report("Reading the selected raster image", bytes.Length, input.Length, "bytes");
            }
            if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision))
            { _shell.StatusMessage = "The document changed while reading the image. Insert it again."; return false; }
            var imageBytes = bytes.ToArray();
            ImageAttachmentImporter.ValidateImage(imageBytes, Path.GetFileName(path));
            if (ImageImportChoicePicker is null && ImageAttachmentPicker is null)
                return await ShowImageInsertionAsync(imageBytes, Path.GetFileName(path), path, statusOperation);
            statusOperation.WaitForUser("Choose whether to link the original image or save a copy");
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
                statusOperation.Complete(_shell.LinkedImagePermissionError, _shell.LinkedImagePermissionError is not null,
                    "Original image linked in buffer; not saved");
                return true;
            }
            return await InsertImageBytesAsync(imageBytes, Path.GetFileName(path), statusOperation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { statusOperation.Fail(ex.Message); _shell.StatusMessage = "Could not insert image: " + Tern.App.Shell.UserProblem.Plain(ex); }
        finally { statusOperation.Cancel(); }
        return false;
    }

    private async Task PasteImageAsync()
    {
        if (!await EnsureImageDocumentAsync()) return;
        using var statusOperation = _shell.BeginStatusOperation("Pasting clipboard images");
        statusOperation.Report("Reading available clipboard image data");
        _shell.StatusMessage = "Reading clipboard image…";
        var tab = _shown!;
        var revision = tab.Session.Revision;
        try
        {
            if (Clipboard is null) { statusOperation.Fail("The clipboard is unavailable."); _shell.StatusMessage = "The clipboard is unavailable."; return; }
            var files = await Clipboard.TryGetFilesAsync();
            if (files is { Length: > 0 })
            {
                var paths = files.Select(file => file.TryGetLocalPath()).ToArray();
                if (paths.Any(path => path is null || !IsRasterImagePath(path)))
                { _shell.StatusMessage = "Copy a PNG, JPEG, GIF, WebP or BMP file in File Explorer, or copy image pixels, then try again."; return; }
                if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision))
                { _shell.StatusMessage = "The document changed while reading the clipboard. Paste again."; return; }
                var inserted = 0;
                foreach (var path in paths)
                {
                    if (!ReferenceEquals(tab, _shown)) break;
                    statusOperation.WaitForUser("Review insertion of each selected clipboard image");
                    if (!await ImportImageFileAsync(path!)) break;
                    inserted++;
                }
                if (inserted == 0) statusOperation.Cancel();
                else statusOperation.Complete($"{inserted} of {paths.Length} images inserted into unsaved buffers. Copied attachments may remain on disk.",
                    inserted < paths.Length, inserted < paths.Length ? "Clipboard image insertion partly completed" : "Clipboard images inserted; not saved");
                return;
            }
            using var bitmap = await Clipboard.TryGetBitmapAsync();
            if (bitmap is null)
            {
                statusOperation.Complete(outcome: "Clipboard contains no supported image; document unchanged");
                _shell.StatusMessage = "No image was pasted. Copy a PNG, JPEG, GIF, WebP or BMP file in File Explorer, or copy image pixels, then use Paste Image as Attachment. Paste text with Ctrl+V.";
                return;
            }
            if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision))
            { _shell.StatusMessage = "The document changed while reading the clipboard. Paste again."; return; }
            if ((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height > ImageAttachmentImporter.MaxPixels)
            { statusOperation.Fail("The clipboard image exceeds 16 million pixels."); _shell.StatusMessage = "The clipboard image exceeds 16 million pixels."; return; }
            using var bytes = new BoundedImageStream();
            statusOperation.Report("Encoding the actual clipboard pixels as PNG");
            await Task.Run(() => bitmap.Save(bytes, new Avalonia.Media.Imaging.PngBitmapEncoderOptions()));
            await InsertImageBytesAsync(bytes.ToArray(), "pasted-image.png", statusOperation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        { statusOperation.Fail(ex.Message); _shell.StatusMessage = "Could not paste clipboard image: " + Tern.App.Shell.UserProblem.Plain(ex); }
        finally { statusOperation.Cancel(); }
    }

    private async Task<bool> InsertImageBytesAsync(byte[] bytes, string suggestedName, ShellViewModel.StatusOperation? parentOperation = null)
    {
        if (_shown is not { IsReadOnly: false, FilePath: not null } tab)
        { _shell.StatusMessage = "Save a writable document before inserting an image."; return false; }
        var revision = tab.Session.Revision;
        var offset = Editor.CaretOffset;
        var directory = Path.GetDirectoryName(tab.FilePath)!;
        if (ImageAttachmentPicker is null) return await ShowImageInsertionAsync(bytes, suggestedName, null, parentOperation);
        if (ImageAttachmentPicker is null && !StorageProvider.CanSave)
        {
            _shell.StatusMessage = "The attachment save picker is unavailable. Your document and existing images were kept.";
            _dialogs?.ShowMessage("Attachment picker unavailable", _shell.StatusMessage);
            return false;
        }
        using var ownedOperation = parentOperation is null ? _shell.BeginStatusOperation("Inserting image attachment", suggestedName) : null;
        var operation = parentOperation ?? ownedOperation!;
        var replacementDeclined = false;
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
            operation.WaitForUser("Choose the image copy destination and filename");
            var selected = await (ImageAttachmentPicker?.Invoke(options) ?? StorageProvider.SaveFilePickerAsync(options));
            if (selected?.TryGetLocalPath() is not { } target)
            { _shell.StatusMessage = "Image insertion cancelled. Your document was kept."; return false; }
            if (!ReferenceEquals(tab, _shown)) { _shell.SetStatus("The active document changed. Try inserting the image again.", Tern.App.Shell.StatusSeverity.Warning); return false; }
            operation.Report("Validating, copying and linking the image");
            await ImageAttachmentImporter.InsertAsync(tab, bytes, Path.GetDirectoryName(target)!, Path.GetFileName(target), offset, revision, allowExternalDestination: true,
                confirmReplace: async destination =>
                {
                    operation.WaitForUser("Confirm replacement of the existing image attachment");
                    var approved = await ConfirmReplaceImageAsync(destination);
                    replacementDeclined = !approved;
                    operation.Report("Continuing the approved image insertion");
                    return approved;
                }, recycleBin: ImageRecycleBin());
            _shell.StatusMessage = "Image copied and linked. Save the document to keep the link. Undo removes the link; the attachment stays on disk.";
            ReportLinkedImagePermissionFailure();
            operation.Complete(_shell.LinkedImagePermissionError, _shell.LinkedImagePermissionError is not null,
                "Image copied and linked in buffer; not saved");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        { if (replacementDeclined) operation.Cancel(); else operation.Fail(ex.Message); _shell.StatusMessage = "Could not insert image: " + Tern.App.Shell.UserProblem.Plain(ex); }
        finally { operation.Cancel(); }
        return false;
    }

    private async Task<bool> ShowImageInsertionAsync(byte[] bytes, string name, string? original, ShellViewModel.StatusOperation? parentOperation = null)
    {
        if (_shown is not { IsReadOnly: false, FilePath: not null } tab) return false;
        using var ownedOperation = parentOperation is null ? _shell.BeginStatusOperation("Inserting reviewed image", name) : null;
        var operation = parentOperation ?? ownedOperation!;
        try
        {
            ImageAttachmentImporter.ValidateImage(bytes, name);
            var revision = tab.Session.Revision; var offset = Editor.CaretOffset; var source = tab.FilePath;
            bool Current() => !_windowClosed && _shell.Tabs.Contains(tab) && ReferenceEquals(tab, _shown) && tab.Session.IsCurrent(revision) && !tab.IsReadOnly && tab.FilePath == source;
            operation.WaitForUser("Review the image, alternative text and copy or link destination");
            var result = await new InsertImageWindow(bytes, name, source, original, async choice =>
            {
                if (!Current()) return "The document changed while choosing the image. Close this form and insert it again.";
                using var attempt = _shell.Activities.Begin("Applying reviewed image insertion", tab.DisplayName);
                var replacementDeclined = false;
                try
                {
                    operation.Report("Applying the approved image insertion");
                    attempt.Report("Validating and copying or linking the selected image");
                    if (choice.Copy)
                        await ImageAttachmentImporter.InsertAsync(tab, bytes, choice.Folder, choice.Name, offset, revision, allowExternalDestination: true, altText: choice.Alt, isCurrent: Current,
                        confirmReplace: async destination =>
                        {
                            operation.WaitForUser("Confirm replacement of the existing image attachment");
                            attempt.WaitForUser("Confirm replacement of the existing image attachment");
                            var approved = await ConfirmReplaceImageAsync(destination);
                            replacementDeclined = !approved;
                            operation.Report("Continuing the approved image insertion");
                            attempt.Report("Continuing the approved image insertion");
                            return approved;
                        }, recycleBin: ImageRecycleBin());
                    else if (original is not null)
                        ImageAttachmentImporter.LinkExisting(tab, bytes, original, offset, revision, choice.Alt);
                    else throw new InvalidOperationException("No original image is available to link; choose a copy destination.");
                    attempt.Complete(outcome: "Image inserted into buffer; not saved; Undo available");
                    return null;
                }
                catch (Exception ex)
                {
                    if (replacementDeclined) attempt.Cancel("Attachment replacement declined; existing attachment kept.");
                    else attempt.Fail("Image insertion did not finish. A copied attachment may remain on disk. " + ex.Message);
                    operation.WaitForUser(replacementDeclined ? "Replacement declined; choose another destination or cancel"
                        : "Image insertion failed; review the error and retry or cancel");
                    throw;
                }
            }, operationFeedback: new LoadingState(_shell.Activities, operation.Id)).ShowDialog<ImageInsertion?>(this);
            if (result is null) return false;
            _shell.StatusMessage = result.Copy ? "Image copied and linked. Undo removes the link; the attachment stays on disk." : "Original image linked. Moving the original will break the link.";
            ReportLinkedImagePermissionFailure();
            operation.Complete(_shell.LinkedImagePermissionError, _shell.LinkedImagePermissionError is not null,
                result.Copy ? "Image copied and linked in buffer; not saved" : "Original image linked in buffer; not saved");
            return true;
        }
        catch (OperationCanceledException) { operation.Cancel(); throw; }
        catch (Exception ex) { operation.Fail(ex.Message); throw; }
        finally { operation.Cancel(); }
    }

    private Tern.App.Shell.ILocalFileDeletion ImageRecycleBin() =>
        LibraryDeletionFactory?.Invoke() ?? new WindowsRecycleBinService(TryGetPlatformHandle()?.Handle ?? 0);

    internal async Task ShowSaveFragmentsAsync()
    {
        var fragments = _shell.SaveFragments;
        if (fragments.Count == 0)
        {
            _shell.SetStatus("No leftover save files were found in the open workspaces.", Tern.App.Shell.StatusSeverity.Info);
            return;
        }

        var recycle = ImageRecycleBin();
        var opened = await new SaveFragmentsWindow(fragments, async fragment =>
        {
            try { await recycle.MoveToRecycleBinAsync(fragment.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
            if (File.Exists(fragment.Path)) return false;
            _shell.ForgetSaveFragment(fragment.Path);
            return true;
        }).ShowDialog<SaveFragmentOpen?>(this);
        if (opened is null) return;
        var copy = _shell.NewDocument();
        copy.ApplyEdits([new Tern.Domain.Text.TextEdit(0, 0, opened.Text)]);
        _shell.SetStatus("Leftover file opened as an unsaved copy. The leftover file and the note are unchanged.", Tern.App.Shell.StatusSeverity.Info);
    }

    private async Task<bool> ConfirmReplaceImageAsync(string target)
    {
        var current = _shown?.FilePath;
        var referencing = _shell.WorkspaceForPath(target)?.Graph.GetBacklinks(target)
            .Select(link => link.SourcePath).Where(source => current is null || !Tern.Workspaces.WorkspacePaths.PathsEqual(source, current))
            .Distinct(Tern.Workspaces.WorkspacePaths.Comparer).Select(Path.GetFileName).OfType<string>().ToList() ?? [];
        return await PromptWindow.AskAsync(OwnedWindows.OfType<InsertImageWindow>().FirstOrDefault(window => window.IsVisible) as Window ?? this, "Replace image file?",
            ImageAttachmentImporter.BuildReplaceMessage(target, referencing),
            ["Replace (old image goes to Recycle Bin)", "Cancel"], 1, 1, destructiveIndex: 0) == 0;
    }

    private async Task<bool> EnsureImageDocumentAsync()
    {
        if (_shown is not { IsReadOnly: false } tab) return false;
        if (tab.FilePath is not null) return true;
        var confirmed = await PromptWindow.AskAsync(this, "Insert image",
            "Save this note before inserting an image\n\nSaving gives Tern a location for image attachments. After the note is saved, choose your image.",
            ["Save document", "Cancel"], 0, 1);
        if (confirmed != 0 || !ReferenceEquals(tab, _shown) || !_shell.Tabs.Contains(tab)) return false;
        _shell.StatusMessage = "Choose where to save the note; image insertion will continue after saving.";
        if (!await _shell.SaveTabAsync(tab))
        {
            _shell.SetStatus("The note was not saved, so no image was inserted. Your draft was kept.", Tern.App.Shell.StatusSeverity.Warning);
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

    private async Task CopyEditorAsync(bool cut)
    {
        if (_shown is not { } tab || Clipboard is not { } clipboard)
        { _shell.SetStatus("Tern could not use the clipboard. Copy the item again and try once more.", Tern.App.Shell.StatusSeverity.Warning); return; }
        var revision = tab.Session.Revision;
        var selectionStart = Editor.SelectionStart;
        var selectionLength = Editor.SelectionLength;
        var start = selectionStart;
        var length = selectionLength;
        if (length == 0 && Editor.Options.CutCopyWholeLine)
        {
            var line = Editor.Document.GetLineByOffset(start);
            start = line.Offset; length = line.TotalLength;
        }
        if (length == 0)
        { _shell.SetStatus("Select text to copy first.", Tern.App.Shell.StatusSeverity.Info); return; }
        var text = Editor.Document.GetText(start, length);
        await clipboard.SetTextAsync(text);
        if (!cut || _windowClosed) return;
        if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision) || !CanRunCommand(Tern.App.Commands.CommandIds.Cut)
            || Editor.SelectionStart != selectionStart || Editor.SelectionLength != selectionLength)
        { _shell.SetStatus("The document or selection changed. Text was copied but not cut; select it again.", Tern.App.Shell.StatusSeverity.Info); return; }
        ApplyEdit(new EditResult([new TextEdit(start, length, "")], start, 0));
    }

    internal async Task PasteEditorAsync(EditorPasteMode mode = EditorPasteMode.Normal)
    {
        if (_shown is not { IsReadOnly: false } tab || Clipboard is null)
        { _shell.StatusMessage = "Choose a writable document before pasting."; return; }
        var revision = tab.Session.Revision;
        var start = Editor.SelectionStart; var length = Editor.SelectionLength;
        var newline = tab.Session.Format.NewLineText;
        bool Current() => !_windowClosed && ReferenceEquals(tab, _shown) && tab.Session.IsCurrent(revision)
            && !tab.IsReadOnly && !_shell.IsBusy && Editor.SelectionStart == start && Editor.SelectionLength == length;
        bool Guard()
        {
            if (Current()) return true;
            _shell.SetStatus("The document or selection changed while reading the clipboard. Paste again.", Tern.App.Shell.StatusSeverity.Info);
            return false;
        }
        try
        {
            using var data = await Clipboard.TryGetDataAsync();
            if (data is null)
            { _shell.SetStatus("The clipboard is empty. Copy some text or an image first.", Tern.App.Shell.StatusSeverity.Info); return; }
            var files = mode == EditorPasteMode.Normal ? await data.TryGetFilesAsync() : null;
            if (files is { Length: > 0 })
            {
                var paths = files.Select(f => f.TryGetLocalPath()).ToArray();
                if (paths.Any(p => p is null || !IsRasterImagePath(p)))
                { _shell.StatusMessage = "Paste image files here. To open Markdown files or folders, use File → Open or drag them into Tern."; return; }
                if (!Guard()) return;
                var problems = new List<string>();
                foreach (var path in paths)
                {
                    if (!ReferenceEquals(tab, _shown)) { problems.Add("The active document changed. Paste remaining images again."); break; }
                    if (!await ImportImageFileAsync(path!)) problems.Add(Path.GetFileName(path) + ": " + _shell.StatusMessage);
                }
                if (problems.Count > 0) _shell.StatusMessage = string.Join(" · ", problems);
                return;
            }
            if (mode == EditorPasteMode.Normal)
            {
                var htmlBytes = await data.TryGetValueAsync(Tern.Desktop.Services.HtmlClipboard.WindowsFormat);
                var windowsHtml = htmlBytes is not null;
                htmlBytes ??= await data.TryGetValueAsync(Tern.Desktop.Services.HtmlClipboard.MimeFormat);
                if (htmlBytes is not null)
                {
                    var markdown = await Task.Run(() => Tern.Rendering.Html.HtmlToMarkdown.Convert(
                        Tern.Desktop.Services.HtmlClipboard.ReadFragment(htmlBytes, windowsHtml), newline));
                    if (!Guard()) return;
                    if (markdown.Length == 0)
                    { _shell.SetStatus("The HTML contains no supported text. Use Paste Plain Text to keep its text alternative.", Tern.App.Shell.StatusSeverity.Info); return; }
                    ApplyEdit(new EditResult([new TextEdit(start, length, markdown)], start + markdown.Length, 0));
                    return;
                }
            }
            var text = await data.TryGetTextAsync();
            if (!Guard()) return;
            if (text is not null)
            {
                if (mode != EditorPasteMode.Plain)
                {
                    var table = await Task.Run(() => MarkdownTable.TryFromDelimited(text, out var parsed) ? parsed : null);
                    if (!Guard()) return;
                    if (table is not null)
                    {
                        var choice = mode == EditorPasteMode.Table ? 0
                            : await PromptWindow.AskAsync(this, "Paste spreadsheet cells",
                                $"The clipboard contains {table.Rows.Count + 1} rows and {table.Columns} columns. Paste them as an aligned Markdown table or keep the original text?",
                                ["Paste as table", "Paste plain text", "Cancel"], cancelIndex: 2);
                        if (choice is not 0 and not 1) return;
                        if (!Guard()) return;
                        if (choice == 0)
                        {
                            ApplyEdit(FormattingService.InsertTable(tab.CurrentText, start, length, newline, table));
                            return;
                        }
                    }
                    else if (mode == EditorPasteMode.Table)
                    {
                        _shell.SetStatus("The clipboard needs at least two rows with consistent CSV or TSV columns. Nothing was pasted.", Tern.App.Shell.StatusSeverity.Info);
                        return;
                    }
                }
                var link = mode == EditorPasteMode.Normal ? Formatting.PasteUrlOverSelection(tab.CurrentText, start, length, text) : null;
                ApplyEdit(link ?? new EditResult([new TextEdit(start, length, text)], start + text.Length, 0));
                return;
            }
            if (mode == EditorPasteMode.Normal) await PasteImageAsync();
            else _shell.SetStatus("The clipboard contains no plain text. Use Paste or Paste Image as Attachment for an image.", Tern.App.Shell.StatusSeverity.Info);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        { _shell.SetStatus("Nothing was pasted. Copy text or a PNG, JPEG, GIF, WebP or BMP image and try again. Details: " + _shell.PlainProblem(ex), Tern.App.Shell.StatusSeverity.Warning); }
    }
}

internal enum EditorPasteMode { Normal, Plain, Table }

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
