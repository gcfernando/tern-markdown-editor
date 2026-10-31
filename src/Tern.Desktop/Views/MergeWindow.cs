using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tern.Desktop.Themes;
using Tern.Domain.Merging;

namespace Tern.Desktop.Views;

/// <summary>
/// Three-way merge dialog: pick Mine, Disk or Both for each conflicting region, compare the three versions side by side,
/// and fine-tune the merged result before accepting it.
/// </summary>
public sealed class MergeWindow : Window
{
    private enum Pick
    {
        Mine,
        Disk,
        Both,
        Base,
    }

    private readonly MergeResult _merge;
    private readonly List<ConflictRegion> _conflicts;
    private readonly Dictionary<ConflictRegion, Pick> _picks = [];
    private readonly ListBox _list = new() { MinWidth = 150 };
    private readonly TextBox _base = Pane("Common ancestor");
    private readonly TextBox _mine = Pane("My version");
    private readonly TextBox _disk = Pane("Version on disk");
    private readonly TextBox _result = new() { AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"), MinHeight = 160 };
    private readonly StackPanel _choices = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private string _generated = string.Empty;

    public MergeWindow(string name, MergeResult merge)
    {
        _merge = merge;
        _conflicts = [.. merge.Regions.OfType<ConflictRegion>()];
        foreach (var conflict in _conflicts)
        {
            _picks[conflict] = Pick.Mine;
        }

        DialogParts.Setup(this, $"Merge changes – {name}", 1000, 700);
        MinWidth = 700;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = DialogStyle.AppIcon();
        this[!BackgroundProperty] = this.GetResourceObservable(ThemeManager.Panel).ToBinding();
        AutomationProperties.SetName(this, "Merge changes");
        AutomationProperties.SetName(_list, "Conflicts");
        AutomationProperties.SetName(_result, "Merged result");

        foreach (var (label, pick) in new[] { ("Keep mine", Pick.Mine), ("Take disk", Pick.Disk), ("Both", Pick.Both), ("Original", Pick.Base) })
        {
            var button = new Button { Content = label };
            AutomationProperties.SetName(button, label);
            button.Click += async (_, _) => await ChooseAsync(pick);
            _choices.Children.Add(button);
        }

        var accept = new Button { Content = "Accept merged text", IsDefault = true };
        accept.Classes.Add("accent");
        AutomationProperties.SetName(accept, "Accept merged text");
        accept.Click += (_, _) => Close(_result.Text ?? string.Empty);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        AutomationProperties.SetName(cancel, "Cancel merge");
        cancel.Click += (_, _) => Close((string?)null);

        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 8, MinHeight = 180 };
        panes.Children.Add(_base);
        panes.Children.Add(_mine);
        panes.Children.Add(_disk);
        Grid.SetColumn(_mine, 1);
        Grid.SetColumn(_disk, 2);

        var summary = new TextBlock
        {
            Text = _conflicts.Count == 0
                ? "No conflicting regions. Both sets of changes combined cleanly."
                : $"{_conflicts.Count} conflicting region(s). Choose how to resolve each one.",
            TextWrapping = TextWrapping.Wrap,
        };

        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,2*"), RowSpacing = 8 };
        // Child minima alone do not constrain star-row allocation. Reserve the
        // comparison and editable-result rows so their controls cannot overflow
        // into the summary or choice buttons when the window fits its content.
        right.RowDefinitions[1].MinHeight = 180;
        right.RowDefinitions[4].MinHeight = 160;
        right.Children.Add(summary);
        right.Children.Add(panes);
        right.Children.Add(_choices);
        right.Children.Add(new TextBlock { Text = "Merged result (editable)", FontWeight = FontWeight.SemiBold });
        right.Children.Add(_result);
        Grid.SetRow(panes, 1);
        Grid.SetRow(_choices, 2);
        Grid.SetRow(right.Children[3], 3);
        Grid.SetRow(_result, 4);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, accept } };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        body.Children.Add(_list);
        body.Children.Add(right);
        Grid.SetColumn(right, 1);

        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 12 };
        root.Children.Add(body);
        root.Children.Add(buttons);
        Grid.SetRow(buttons, 1);
        Content = root;

        _list.ItemsSource = _conflicts.Select((_, i) => $"Conflict {i + 1}").ToList();
        _list.SelectionChanged += (_, _) => ShowSelected();
        _choices.IsVisible = _conflicts.Count > 0;
        _list.IsVisible = _conflicts.Count > 0;
        _list.SelectedIndex = _conflicts.Count > 0 ? 0 : -1;
        Rebuild();
    }

    private static TextBox Pane(string name)
    {
        var box = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Code, Consolas, monospace") };
        AutomationProperties.SetName(box, name);
        box.PlaceholderText = name;
        return box;
    }

    private void ShowSelected()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _conflicts.Count)
        {
            return;
        }

        var conflict = _conflicts[_list.SelectedIndex];
        _base.Text = conflict.Base;
        _mine.Text = conflict.Mine;
        _disk.Text = conflict.Disk;
    }

    private async Task ChooseAsync(Pick pick)
    {
        if (_result.Text != _generated && await PromptWindow.AskAsync(this, "Replace manual merge edits?",
            "Changing this choice rebuilds the merged result and replaces your manual edits. Cancel to keep your text.",
            ["Rebuild result", "Cancel"], 1, 1) != 0) return;
        if (_list.SelectedIndex >= 0)
        {
            _picks[_conflicts[_list.SelectedIndex]] = pick;
            Rebuild();
        }
    }

    private void Rebuild() => _result.Text = _generated = _merge.Render(c => _picks[c] switch
    {
        Pick.Disk => c.Disk,
        Pick.Both => c.Mine + c.Disk,
        Pick.Base => c.Base,
        _ => c.Mine,
    });
}
