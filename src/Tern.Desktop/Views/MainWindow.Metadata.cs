using Avalonia.Controls;
using Avalonia.Interactivity;
using Tern.App.Commands;
using Tern.App.Editing;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private async void OnEditTagsClick(object? sender, RoutedEventArgs e)
    {
        if (_shown is not { IsReadOnly: false, Analysis: { } analysis } tab) return;
        var revision = tab.Session.Revision;
        var dialog = new Window(); DialogParts.Setup(dialog, "Edit note metadata", 600, 420);
        var input = new TextBox { Text = string.Join(", ", FrontMatterReader.Read(analysis.FrontMatter?.Yaml).Tags) };
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var apply = new Button { Content = "Apply tags", IsDefault = true };
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
            { preview.Text = "The note changed. Close this form and reopen Edit tags to refresh it."; apply.IsEnabled = false; return; }
            if (TagEdits.Create(tab.CurrentText, analysis, input.Text ?? "", tab.Session.Format.NewLineText) is { } edit)
            { tab.ApplyEdits([edit]); dialog.Close(); }
        };
        dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12, Children =
        {
            new TextBlock { Text = "Add / edit tags. A metadata block is inserted when the note has none.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            DialogParts.Labeled("Tags (comma separated)", input), preview, GuideParts.Actions(source, cancel, apply)
        } };
        Update(); await dialog.ShowDialog(this);
    }
}
