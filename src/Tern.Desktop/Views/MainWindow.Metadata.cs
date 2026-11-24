using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tern.App.Commands;
using Tern.App.Editing;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private bool _editingMetadata;
    private async void OnEditTagsClick(object? sender, RoutedEventArgs e)
    {
        if (_editingMetadata || _shown is not { IsReadOnly: false } tab) return;
        _editingMetadata = true;
        try
        {
        var revision = tab.Session.Revision;
        var snapshot = tab.CurrentText;
        var profile = tab.Profile;
        // The inspector's asynchronous analysis can still describe an earlier
        // revision. Never use those ranges to edit the current front matter.
        var analysis = await Task.Run(() => Tern.Markdown.MarkdownAnalyzer.Analyze(snapshot, profile));
        if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision) || tab.IsReadOnly || _windowClosed)
        { _shell.StatusMessage = "The document changed. Reopen Edit tags to refresh it."; return; }
        var dialog = new Window(); DialogParts.Setup(dialog, "Edit tags", 600, 340, DialogGuidance.Metadata, guidance: false);
        dialog.CanResize = false;
        var input = new TextBox { Text = string.Join(", ", FrontMatterReader.Read(analysis.FrontMatter?.Yaml).Tags) };
        var message = GuideParts.Caption("");
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var apply = new Button { Content = "Apply tags", IsDefault = true, Classes = { "accent" } };
        var source = new Button { Content = "Edit metadata in Source" };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        void Update()
        {
            var edit = TagEdits.Create(tab.CurrentText, analysis, input.Text ?? "", tab.Session.Format.NewLineText);
            apply.IsEnabled = edit is not null;
            preview.Text = edit is null ? "This metadata is complex or the tags contain unsupported punctuation. Edit in Source to preserve it."
                : "Metadata lives in this Markdown file. This change stays unsaved until you choose Save.\n\n" + edit.Value.NewText;
        }
        input.PropertyChanged += (_, change) => { if (change.Property == TextBox.TextProperty) Update(); };
        cancel.Click += (_, _) => dialog.Close();
        source.Click += async (_, _) => { dialog.Close(); if (ReferenceEquals(tab, _shown)) { await RunCommandAsync(CommandIds.ViewSource); GoToLine(1, 0); } };
        apply.Click += (_, _) =>
        {
            if (!ReferenceEquals(tab, _shown) || !tab.Session.IsCurrent(revision) || tab.IsReadOnly)
            { message.Text = "The document changed. Close and reopen Edit tags to refresh it."; message.IsVisible = true; apply.IsEnabled = false; return; }
            if (TagEdits.Create(tab.CurrentText, analysis, input.Text ?? "", tab.Session.Format.NewLineText) is { } edit)
            { tab.ApplyEdits([edit]); dialog.Close(); }
        };
        var clear = new Button { Content = "Clear tags" };
        clear.Click += (_, _) => { input.Text = ""; input.Focus(); };
        preview.IsVisible = false;
        input.TextChanged += (_, _) => { message.Text = apply.IsEnabled ? "" : "This metadata is complex. Edit it in Source to preserve it."; message.IsVisible = !apply.IsEnabled; };
        var field = new Grid { ColumnDefinitions = new("*,Auto"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(8).ToBinding(), Children = { input, clear } };
        Grid.SetColumn(clear, 1);
        var fields = GuideParts.Vertical(GuideParts.Caption("Separate tags with commas, for example work, planning."), field,
            GuideParts.Body("Apply tags edits this note. Save (Ctrl+S) writes it to disk."), message);
        dialog.Content = GuideParts.Frame(GuideParts.Title("Edit tags"), new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, GuideParts.Actions(apply, cancel, source));
        Update(); message.Text = apply.IsEnabled ? "" : "This metadata is complex. Edit it in Source to preserve it."; message.IsVisible = !apply.IsEnabled;
        await dialog.ShowDialog(this);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { _shell.StatusMessage = "Could not edit note metadata: " + _shell.PlainProblem(ex); }
        finally { _editingMetadata = false; }
    }
}
