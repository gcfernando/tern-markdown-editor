using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Tern.App.Shell;

namespace Tern.Desktop.Views;

/// <summary>Scoped reset choices keep source documents and open work separate from application state.</summary>
public sealed class ResetWindow : Window
{
    public ResetWindow(ShellViewModel shell)
    {
        DialogParts.Setup(this, "Reset Tern", 760, 680);
        MinWidth = 480;
        MinHeight = 420;
        var recent = true; var preferences = false; var session = false; var caches = false; var remote = false;
        var status = GuideParts.Caption("");
        AutomationProperties.SetName(status, "Reset selection status");
        var reset = new Button { Content = "Reset selected" };
        AutomationProperties.SetName(reset, "Reset selected application data");
        reset.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();
        void Update()
        {
            var count = (recent ? 1 : 0) + (preferences ? 1 : 0) + (session ? 1 : 0) + (caches ? 1 : 0) + (remote ? 1 : 0);
            status.Text = $"{count} categories selected. Your documents and open drafts are preserved.";
            var hasCachedTabs = shell.Tabs.Any(t => (t.ResourceDocumentPath ?? t.FilePath) is { } path && path.StartsWith(Path.GetFullPath(Path.Combine(shell.Paths.StateDirectory, "remote-cache")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            reset.IsEnabled = count > 0 && !shell.IsBusy && !shell.IsIndexing && !shell.HasActiveStatusOperations
                && (!caches || shell.OpenWorkspaces.Count == 0) && (!remote || !hasCachedTabs);
            if (shell.IsBusy || shell.IsIndexing || shell.HasActiveStatusOperations) status.Text = "Wait for the current loading, saving, download or indexing operation to finish before resetting application data.";
            else if (caches && shell.OpenWorkspaces.Count > 0) status.Text = "Close all workspaces and browsed folders before resetting search indexes.";
            else if (remote && hasCachedTabs) status.Text = "Close cached online documents before clearing remote downloads.";
        }
        Control Choice(string title, string detail, bool value, Action<bool> apply)
        {
            var box = DialogParts.Check(title, value, v => { apply(v); Update(); });
            return GuideParts.Card(GuideParts.Vertical(box, GuideParts.Body(detail)));
        }
        reset.Click += (_, _) =>
        {
            if (shell.ResetApplication(new ResetOptions(recent, preferences, session, caches, remote))) Close();
            else
            {
                status.Text = shell.StatusMessage;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => status.BringIntoView(),
                    Avalonia.Threading.DispatcherPriority.Loaded);
            }
        };
        var choices = GuideParts.Vertical(
            Choice("Recent history (including pinned references)", "Forget recent file and folder references. Workspace contents and open tabs are independent; physical documents stay on disk.", true, v => recent = v),
            Choice("Preferences and keyboard shortcuts", "Restore application defaults and default command bindings. Document text, attachments and saved files are preserved.", false, v => preferences = v),
            Choice("Session restore list", "Forget the saved list used to reopen document tabs and workspace roots. Open drafts and recovery copies remain available.", false, v => session = v),
            Choice("Search indexes (close all workspaces first)", "Remove rebuildable search-index data after closing all workspaces and browsed folders. Source files are preserved; reopening each root builds its index again.", false, v => caches = v),
            Choice("Cached online documents and assets", "Remove complete downloaded cache entries after closing their document tabs. Files you explicitly downloaded elsewhere, attachments and recovery copies are preserved.", false, v => remote = v));
        var body = GuideParts.Vertical(choices,
            GuideParts.Body("Choose what to refresh. Keep your writing."),
            GuideParts.Caption("Documents, folders, attachments, open drafts and recovery copies are preserved. Saved AI connections and credentials are managed separately in AI Connections."), status);
        Content = GuideParts.Frame(GuideParts.Title("Reset Tern"), new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        }, GuideParts.Actions(cancel, reset));
        void OnPrerequisiteChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ShellViewModel.IsBusy) or nameof(ShellViewModel.IsIndexing) or nameof(ShellViewModel.HasActiveStatusOperations)) Update();
        }
        void OnCollectionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Update();
        shell.PropertyChanged += OnPrerequisiteChanged;
        shell.OpenWorkspaces.CollectionChanged += OnCollectionsChanged;
        shell.Tabs.CollectionChanged += OnCollectionsChanged;
        Closed += (_, _) =>
        {
            shell.PropertyChanged -= OnPrerequisiteChanged;
            shell.OpenWorkspaces.CollectionChanged -= OnCollectionsChanged;
            shell.Tabs.CollectionChanged -= OnCollectionsChanged;
        };
        Update();
    }
}
