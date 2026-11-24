using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Tern.Filesystem;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

public sealed record SaveFragmentOpen(SaveFragment Fragment, string Text);

/// <summary>Lists files left by interrupted saves; each can be opened as an unsaved copy, compared with its note, or moved to the Recycle Bin (R-06).</summary>
public sealed class SaveFragmentsWindow : Window
{
    private readonly System.Collections.ObjectModel.ObservableCollection<SaveFragment> _items;

    public SaveFragmentsWindow(IReadOnlyList<SaveFragment> fragments, Func<SaveFragment, Task<bool>> deleteAsync)
    {
        _items = new(fragments);
        DialogParts.Setup(this, "Recovered save fragments", 1020, 640, DialogGuidance.SaveFragments);
        MinWidth = 700;
        MinHeight = 450;
        var list = new ListBox
        {
            ItemsSource = _items,
            ItemTemplate = new FuncDataTemplate<SaveFragment>((item, _) =>
            {
                var name = GuideParts.Body(Path.GetFileName(item.OriginalPath));
                name.FontWeight = FontWeight.SemiBold;
                var kind = item.Kind == SaveFragmentKind.SaveBackup ? "Preserved competing version" : "Unfinished copy";
                return GuideParts.Vertical(name, GuideParts.Caption($"{kind} · {item.LastWriteUtc.ToLocalTime():g}"));
            }),
        };
        AutomationProperties.SetName(list, "Leftover save files");
        var diff = new DiffView("Note on disk", "Leftover file");
        var status = MessageTones.Follow(GuideParts.Caption(string.Empty));
        var open = new Button { Content = "Open as copy", IsEnabled = false, Classes = { "accent" } };
        var compare = new Button { Content = "Compare", IsEnabled = false };
        var delete = new Button { Content = "Move to Recycle Bin", IsEnabled = false, Classes = { "danger" } };
        var close = new Button { Content = "Close", IsCancel = true };
        AutomationProperties.SetName(open, "Open the selected leftover file as an unsaved copy");
        AutomationProperties.SetName(compare, "Compare the selected leftover file with its note");
        AutomationProperties.SetName(delete, "Move the selected leftover file to the Recycle Bin");
        close.Click += (_, _) => Close((SaveFragmentOpen?)null);

        async Task<string?> ReadAsync(string path)
        {
            try { return (await new DocumentFileService().LoadAsync(path)).Text; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { status.Text = "Could not read " + Path.GetFileName(path) + ": " + Tern.App.Shell.UserProblem.Plain(ex); return null; }
        }

        list.SelectionChanged += async (_, _) =>
        {
            var selected = list.SelectedItem as SaveFragment;
            open.IsEnabled = compare.IsEnabled = delete.IsEnabled = selected is not null;
            status.Text = string.Empty;
            if (selected is null) { diff.Show(null, string.Empty, "Select a leftover file."); return; }
            var text = await ReadAsync(selected.Path);
            if (list.SelectedItem == selected && text is not null) diff.Show(null, text, "Choose Compare to see how this file differs from the note.");
        };
        compare.Click += async (_, _) =>
        {
            if (list.SelectedItem is not SaveFragment selected) return;
            var fragment = await ReadAsync(selected.Path);
            if (fragment is null || !ReferenceEquals(list.SelectedItem, selected)) return;
            if (!File.Exists(selected.OriginalPath)) { diff.Show(null, fragment, "The note this file belongs to no longer exists."); return; }
            var note = await ReadAsync(selected.OriginalPath);
            // The user may have picked another file while this one was loading.
            if (note is not null && ReferenceEquals(list.SelectedItem, selected)) diff.Show(note, fragment);
        };
        open.Click += async (_, _) =>
        {
            if (list.SelectedItem is not SaveFragment selected) return;
            if (await ReadAsync(selected.Path) is { } text) Close(new SaveFragmentOpen(selected, text));
        };
        delete.Click += async (_, _) =>
        {
            if (list.SelectedItem is not SaveFragment selected) return;
            var confirm = await PromptWindow.AskAsync(this, "Move leftover file to the Recycle Bin?",
                $"{Path.GetFileName(selected.Path)} is a full copy of {Path.GetFileName(selected.OriginalPath)} from an interrupted save. The note itself is not changed.",
                ["Move to Recycle Bin", "Keep"], 1, 1, destructiveIndex: 0);
            if (confirm != 0) return;
            if (await deleteAsync(selected)) _items.Remove(selected);
            else status.Text = "The file could not be moved to the Recycle Bin and was left in place.";
            if (_items.Count == 0) Close((SaveFragmentOpen?)null);
        };

        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(12).ToBinding() };
        panes.Children.Add(list);
        Grid.SetColumn(diff, 1);
        panes.Children.Add(diff);
        var header = GuideParts.Vertical(GuideParts.Title("Recovered save fragments"),
            GuideParts.Body("An interrupted save can leave a hidden copy of a note next to it; these also sync to other devices. Compare, keep as a copy, or move them to the Recycle Bin."));
        Content = GuideParts.Frame(header, panes, GuideParts.Vertical(status, GuideParts.Actions(open, close, delete, compare)));
        Opened += (_, _) => list.SelectedIndex = 0;
    }
}
