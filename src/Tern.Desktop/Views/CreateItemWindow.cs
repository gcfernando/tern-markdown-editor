using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public enum CreateItemKind { File, Folder, Workspace }
public sealed record CreatedItemRequest(string Parent, string Name);

/// <summary>One reviewable creation form: the parent and name are chosen together.</summary>
public sealed class CreateItemWindow : Window
{
    private readonly TextBox _parent;
    private readonly TextBox _name;
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _create = new() { Content = "Create", IsDefault = true };
    private readonly CreateItemKind _kind;
    private readonly Func<string, string?>? _validateParent;

    public CreateItemWindow(CreateItemKind kind, string initialParent, bool outside,
        Func<string, string?>? validateParent = null)
    {
        _kind = kind;
        _validateParent = validateParent;
        var noun = kind switch { CreateItemKind.File => "file", CreateItemKind.Folder => "folder", _ => "workspace" };
        DialogParts.Setup(this, "Create " + noun + (outside ? " outside workspaces" : ""), 560, 440);
        _parent = new TextBox { Text = initialParent, IsReadOnly = !outside };
        _name = new TextBox { Text = kind == CreateItemKind.File ? "New document.md" : "New " + noun };
        AutomationProperties.SetName(_parent, "Parent folder");
        AutomationProperties.SetName(_name, kind == CreateItemKind.File ? "File name" : kind == CreateItemKind.Folder ? "Folder name" : "Workspace name");
        AutomationProperties.SetName(_error, "Creation validation error");
        AutomationProperties.SetName(_preview, "Full creation path");
        AutomationProperties.SetName(_create, "Create");
        var browse = new Button { Content = "Browse…", IsVisible = outside };
        AutomationProperties.SetName(browse, "Choose parent folder");
        browse.Click += async (_, _) =>
        {
            if (!StorageProvider.CanPickFolder) { ShowError("The folder picker is unavailable. Enter the parent folder path instead."); return; }
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                { Title = "Choose the parent folder (the new item will be created inside it)", AllowMultiple = false });
                if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) _parent.Text = path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { ShowError("Could not choose a folder: " + ex.Message); }
        };
        var location = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        location.Children.Add(_parent); location.Children.Add(browse); Grid.SetColumn(browse, 1);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        AutomationProperties.SetName(cancel, "Cancel creation");
        cancel.Click += (_, _) => Close((CreatedItemRequest?)null);
        _create.Classes.Add("accent");
        _create.Click += (_, _) =>
        {
            var request = GetRequest(out var problem);
            if (request is null) { ShowError(problem!); return; }
            Close(request);
        };
        var fields = GuideParts.Vertical(
            new TextBlock { Text = "Parent folder", FontWeight = FontWeight.SemiBold }, location,
            new TextBlock { Text = kind == CreateItemKind.File ? "File name" : kind == CreateItemKind.Folder ? "Folder name" : "Workspace name", FontWeight = FontWeight.SemiBold }, _name,
            _preview);
        if (kind == CreateItemKind.Workspace)
            fields.Children.Add(new TextBlock { Text = "Creates a new directory with workspace metadata. Existing directories are kept unchanged.", TextWrapping = TextWrapping.Wrap });
        var scroll = new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var actions = GuideParts.Actions(cancel, _create);
        var footer = GuideParts.Vertical(_error, actions);
        var frame = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 16, Children = { scroll, footer } };
        Grid.SetRow(footer, 1); Content = frame;
        frame.SizeChanged += (_, _) => MinHeight = Math.Min(MaxHeight, footer.DesiredSize.Height + 120);
        _parent.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Refresh(); };
        _name.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Refresh(); };
        Opened += (_, _) => { _name.Focus(); _name.SelectAll(); };
        Refresh();
    }

    private CreatedItemRequest? GetRequest(out string? error)
    {
        error = null;
        var name = _name.Text ?? string.Empty;
        var valid = FileNameValidator.Validate(name);
        if (!valid.IsValid) { error = valid.Message; return null; }
        if (_kind == CreateItemKind.File && Path.GetExtension(name).Length == 0) name += ".md";
        try
        {
            var input = _parent.Text ?? string.Empty;
            if (!Path.IsPathFullyQualified(input)) { error = "Enter the full path of an existing parent folder."; return null; }
            var parent = Path.GetFullPath(input);
            if (!Directory.Exists(parent)) { error = "The parent folder does not exist. Choose an existing folder."; return null; }
            if (_validateParent?.Invoke(parent) is { } problem) { error = problem; return null; }
            var path = Path.Combine(parent, name);
            if (Directory.Exists(path) || File.Exists(path)) { error = "That name already exists in this parent folder. Choose another name."; return null; }
            return new(parent, name);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { error = "Cannot use this location: " + ex.Message; return null; }
    }

    private void Refresh()
    {
        var request = GetRequest(out var problem);
        _create.IsEnabled = request is not null;
        _preview.Text = request is null ? string.Empty : "Will create: " + Path.Combine(request.Parent, request.Name);
        _error.Text = problem;
        _error.IsVisible = problem is not null;
        if (IsVisible)
        {
            DialogParts.FitContentHeight(this, 440);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (IsVisible && _name.IsKeyboardFocusWithin) _name.BringIntoView();
                else if (IsVisible && _parent.IsKeyboardFocusWithin) _parent.BringIntoView();
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private void ShowError(string message)
    {
        _error.Text = message; _error.IsVisible = true;
        if (IsVisible) DialogParts.FitContentHeight(this, 440);
    }
}
