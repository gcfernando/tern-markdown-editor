using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Tern.Desktop.Views;

internal sealed record ImageInsertion(bool Copy, string Alt, string Folder, string Name);

internal sealed class InsertImageWindow : Window
{
    private readonly Bitmap _thumbnail;
    internal TextBox AltInput { get; } = new();
    internal TextBox NameInput { get; } = new();
    internal TextBox FolderInput { get; } = new();
    internal CheckBox CopyChoice { get; } = new() { Content = "Copy into document assets (portable)", IsChecked = true };
    internal Button InsertButton { get; } = new() { Content = "Insert image", IsDefault = true };
    private readonly TextBlock _destination = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = MessageTones.Follow(new() { TextWrapping = TextWrapping.Wrap });
    private bool _submitting;

    public InsertImageWindow(byte[] bytes, string name, string note, string? original, Func<ImageInsertion, Task<string?>>? submit = null, Control? operationFeedback = null)
    {
        DialogParts.Setup(this, "Insert image", 620, 630, DialogGuidance.Image);
        using var stream = new MemoryStream(bytes, writable: false);
        _thumbnail = Bitmap.DecodeToWidth(stream, 480);
        AltInput.Text = Path.GetFileNameWithoutExtension(name);
        FolderInput.Text = Path.Combine(Path.GetDirectoryName(note)!, "assets");
        NameInput.Text = Services.ImageAttachmentImporter.SuggestUniqueName(FolderInput.Text, name);
        CopyChoice.IsEnabled = original is not null;
        var browse = new Button { Content = "Choose copy folder…" };
        browse.Click += async (_, _) =>
        {
            if (!StorageProvider.CanPickFolder) return;
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Image destination", AllowMultiple = false });
                if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) FolderInput.Text = path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
            { _error.Text = "Could not open the folder picker. Type the folder path instead. " + Tern.App.Shell.UserProblem.Plain(ex); }
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var destinationHost = new ContentControl { Content = _destination };
        cancel.Click += (_, _) => Close(null);
        InsertButton.Click += async (_, _) =>
        {
            if (_submitting || !InsertButton.IsEnabled) return;
            var choice = new ImageInsertion(CopyChoice.IsChecked == true, AltInput.Text ?? "", FolderInput.Text!, NameInput.Text!);
            if (submit is null) { Close(choice); return; }
            _submitting = true;
            InsertButton.IsEnabled = cancel.IsEnabled = AltInput.IsEnabled = NameInput.IsEnabled = FolderInput.IsEnabled = CopyChoice.IsEnabled = browse.IsEnabled = false;
            try
            {
                if (operationFeedback is not null) destinationHost.Content = operationFeedback;
                var error = await submit(choice);
                if (error is null) { _submitting = false; Close(choice); return; }
                _error.Text = error;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { _error.Text = "The image could not be inserted. " + Tern.App.Shell.UserProblem.Plain(ex); }
            finally
            {
                destinationHost.Content = _destination;
                _submitting = false; cancel.IsEnabled = AltInput.IsEnabled = true; CopyChoice.IsEnabled = original is not null;
                Update();
            }
        };
        Closing += (_, e) => { if (_submitting) e.Cancel = true; };
        var image = new Image { Source = _thumbnail, Height = 150, Stretch = Stretch.Uniform };
        AutomationProperties.SetName(image, "Selected image thumbnail");
        var form = new StackPanel { [!StackPanel.SpacingProperty] = GuideParts.Space(8).ToBinding(), Children =
        {
            image, DialogParts.Labeled("Description (alt text)", AltInput), CopyChoice,
            new TextBlock { Text = original is null ? "Clipboard image: a copy is required." : "Uncheck to link the original. Moving that original will break the link.", TextWrapping = TextWrapping.Wrap },
            DialogParts.Labeled("Filename", NameInput), DialogParts.Labeled("Copy destination", FolderInput), browse, destinationHost, _error,
            new TextBlock { Text = "Undo removes the Markdown link. A copied image may remain on disk.", TextWrapping = TextWrapping.Wrap },
        } };
        Content = GuideParts.FrameWithReachableFooter(this, GuideParts.Title("Insert image"),
            new ScrollViewer { Content = form, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            GuideParts.Actions(InsertButton, cancel));
        void Update()
        {
            if (_submitting) return;
            var copy = CopyChoice.IsChecked == true;
            NameInput.IsEnabled = FolderInput.IsEnabled = browse.IsEnabled = copy;
            InsertButton.IsEnabled = !copy || Path.IsPathFullyQualified(FolderInput.Text ?? "") && Workspaces.FileNameValidator.Validate(NameInput.Text ?? "").IsValid;
            _destination.Text = copy ? "Copy to: " + Path.Combine(FolderInput.Text ?? "", NameInput.Text ?? "") : "Link original: " + original;
        }
        foreach (var input in new[] { FolderInput, NameInput }) input.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Update(); };
        CopyChoice.IsCheckedChanged += (_, _) => Update();
        Closed += (_, _) => _thumbnail.Dispose();
        Update();
    }
}
