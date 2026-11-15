using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Tern.Desktop.Services;
using Tern.Export;
using Tern.Rendering.Preview;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private async void OnMenuCommandClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string id })
        {
            e.Handled = true;
            await RunCommandAsync(id);
        }
    }

    internal async Task CreateWorkspaceEntryAsync(bool folder, string? requestedName = null)
    {
        if (_shell.Workspace is null) await OpenFolderAsync();
        if (_shell.Workspace is not { } workspace) return;
        var selected = FileTree.SelectedItem as Tern.Desktop.ViewModels.TreeRow;
        var selectedOwner = selected is null ? null : _shell.OpenWorkspaces.FirstOrDefault(w => string.Equals(w.Root, selected.FullPath, StringComparison.OrdinalIgnoreCase))
            ?? _shell.WorkspaceForPath(selected.FullPath);
        if (selectedOwner is { } owner)
        {
            workspace = owner;
            _shell.SelectWorkspace(owner);
        }
        var parent = selected is null ? workspace.Root : selected.IsDirectory ? selected.FullPath : Path.GetDirectoryName(selected.FullPath)!;
        if (requestedName is null)
        {
            await ShowCreationFormAsync(folder ? CreateItemKind.Folder : CreateItemKind.File, parent, workspace);
            return;
        }
        var name = requestedName;
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!folder && Path.GetExtension(name).Length == 0) name += ".md";
        var result = await Task.Run(() => folder ? workspace.FileOperations.CreateFolder(parent, name) : workspace.FileOperations.CreateFile(parent, name));
        if (!result.Succeeded)
        {
            _shell.StatusMessage = result.Message ?? "Could not create the item.";
            if (_dialogs is not null) _dialogs.ShowMessage("Could not create " + (folder ? "folder" : "file"), _shell.StatusMessage);
            return;
        }

        await workspace.RescanTreeAsync();
        if (ReferenceEquals(workspace, _shell.Workspace))
        {
            RefreshWorkspace();
            await ShowNavAsync("Files");
            if (!folder && result.Path is { } path) await _shell.OpenFileAsync(path);
        }
        _shell.StatusMessage = "Created " + name;
    }

    private async Task PrintDocumentAsync()
    {
        if (_shown is not { } tab) return;
        var snapshot = tab.Session.GetSnapshot();
        var options = ExportDefaults.PreviewOptions with { Profile = tab.Profile, DocumentPath = tab.FilePath ?? tab.ResourceDocumentPath, WorkspaceRoot = WorkspaceRootFor(tab),
            Resolver = new Tern.Rendering.Resources.ResourceResolverOptions { AdditionalImageFiles = tab.LinkedImageFiles } };
        await new PrintPreviewWindow(tab.DisplayName, snapshot.Text, options,
            _remoteImages is null ? null : _remoteImages.FetchForExport).ShowDialog(this);
    }
}

public sealed class FileNameWindow : Window
{
    public FileNameWindow(bool folder, string parent, bool rename = false, string? initialName = null, string? dialogTitle = null, Func<string, string?>? validateName = null)
    {
        DialogParts.Setup(this, dialogTitle ?? (rename ? "Rename document" : folder ? "New folder" : "New Markdown file"), 480, 360);
        var input = new TextBox { Text = initialName ?? (folder ? "New folder" : "New document.md") };
        Avalonia.Automation.AutomationProperties.SetName(input, folder ? "Folder name" : "File name");
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
        Avalonia.Automation.AutomationProperties.SetName(status, "Filename validation error");
        status[!TextBlock.ForegroundProperty] = status.GetResourceObservable(Tern.Desktop.Themes.ThemeManager.Danger).ToBinding();
        var create = new Button { Content = rename ? "Rename" : "Create", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        string? ValidationError()
        {
            var validation = FileNameValidator.Validate(input.Text ?? string.Empty);
            return validation.IsValid ? validateName?.Invoke(input.Text ?? string.Empty) : validation.Message;
        }
        void RefreshValidation()
        {
            var error = ValidationError();
            status.Text = error;
            status.IsVisible = error is not null;
            create.IsEnabled = error is null;
            if (IsVisible)
            {
                DialogParts.FitContentHeight(this, 360);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (IsVisible && input.IsKeyboardFocusWithin) input.BringIntoView();
                }, Avalonia.Threading.DispatcherPriority.Loaded);
            }
        }
        create.Click += (_, _) =>
        {
            // Revalidate at execution as the filesystem may have changed after typing.
            if (ValidationError() is not null) { RefreshValidation(); input.Focus(); return; }
            Close(input.Text);
        };
        cancel.Click += (_, _) => Close((string?)null);
        var location = new TextBlock { Text = parent, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } };
        Avalonia.Automation.AutomationProperties.SetName(location, "Target folder or document context");
        ToolTip.SetTip(location, parent);
        var label = dialogTitle?.Contains("workspace", StringComparison.OrdinalIgnoreCase) == true
            ? "Workspace name" : folder ? "Folder name" : "File name";
        var fields = GuideParts.Vertical(location, new TextBlock { Text = label, FontWeight = Avalonia.Media.FontWeight.SemiBold }, input);
        var scroll = new ScrollViewer
        {
            Content = fields,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        var actions = GuideParts.Actions(cancel, create);
        var footer = GuideParts.Vertical(status, actions);
        var frame = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 16, Children = { scroll, footer } };
        Grid.SetRow(footer, 1);
        frame.SizeChanged += (_, _) => MinHeight = Math.Min(MaxHeight, footer.DesiredSize.Height + 100);
        Content = frame;
        input.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) RefreshValidation(); };
        Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        RefreshValidation();
    }
}
