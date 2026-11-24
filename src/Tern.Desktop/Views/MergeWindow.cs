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
        Unreviewed,
        Mine,
        Disk,
        Both,
        Base,
        Manual,
    }

    private readonly MergeResult _merge;
    private readonly List<ConflictRegion> _conflicts;
    private readonly Dictionary<ConflictRegion, Pick> _picks = new(ReferenceEqualityComparer.Instance);
    private readonly ListBox _list = new() { MinWidth = 150 };
    private readonly TextBox _base = Pane("Common ancestor");
    private readonly TextBox _mine = Pane("My version");
    private readonly TextBox _disk = Pane("Version on disk");
    private readonly TextBox _result = new() { AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"), MinHeight = 160 };
    private readonly StackPanel _choices = new() { Orientation = Orientation.Horizontal, [!StackPanel.SpacingProperty] = GuideParts.Space(4).ToBinding() };
    private string _generated = string.Empty;
    private readonly Button _accept = new() { Content = "Accept merged text", IsDefault = true, Classes = { "accent" } };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _differences = Pane("Selected conflict line changes");

    public MergeWindow(string name, MergeResult merge, bool savesOnAccept = false)
    {
        _merge = merge;
        _conflicts = [.. merge.Regions.OfType<ConflictRegion>()];
        foreach (var conflict in _conflicts)
        {
            _picks[conflict] = Pick.Unreviewed;
        }

        DialogParts.Setup(this, $"Merge changes — {name}", 1000, 700, DialogGuidance.Merge);
        MinWidth = 700;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = DialogStyle.AppIcon();
        this[!BackgroundProperty] = this.GetResourceObservable(ThemeManager.Panel).ToBinding();
        AutomationProperties.SetName(this, "Merge changes");
        AutomationProperties.SetName(_list, "Conflicts");
        AutomationProperties.SetName(_result, "Merged result");
        AutomationProperties.SetName(_summary, "Merge review progress");

        foreach (var (label, pick) in new[] { ("Keep mine", Pick.Mine), ("Take disk", Pick.Disk), ("Both", Pick.Both), ("Original", Pick.Base) })
        {
            var button = new Button { Content = label };
            AutomationProperties.SetName(button, label);
            button.Click += async (_, _) => await ChooseAsync(pick);
            _choices.Children.Add(button);
        }

        AutomationProperties.SetName(_accept, "Accept merged text");
        _accept.Click += (_, _) => { if (_picks.Values.All(p => p != Pick.Unreviewed)) Close(_result.Text ?? string.Empty); };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        AutomationProperties.SetName(cancel, "Cancel merge");
        cancel.Click += (_, _) => Close((string?)null);

        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(8).ToBinding(), MinHeight = 180 };
        panes.Children.Add(LabeledPane("Original saved text", _base));
        var minePane = LabeledPane("Your current edits", _mine);
        var diskPane = LabeledPane("Changed file on disk", _disk);
        panes.Children.Add(minePane);
        panes.Children.Add(diskPane);
        Grid.SetColumn(minePane, 1);
        Grid.SetColumn(diskPane, 2);

        var previous = new Button { Content = "Previous", Padding = new Thickness(4) };
        var next = new Button { Content = "Next", Padding = new Thickness(4) };
        AutomationProperties.SetName(previous, "Previous conflict");
        AutomationProperties.SetName(next, "Next conflict");
        previous.Click += (_, _) => Step(-1);
        next.Click += (_, _) => Step(1);
        var manual = new Button { Content = "Mark this conflict resolved", HorizontalContentAlignment = HorizontalAlignment.Stretch };
        manual.Content = new TextBlock { Text = "Mark this conflict resolved", TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(manual, "Mark this conflict resolved manually");
        manual.Click += (_, _) =>
        {
            if (_list.SelectedIndex < 0) return;
            _picks[_conflicts[_list.SelectedIndex]] = Pick.Manual;
            UpdateReview();
        };

        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,2*"), [!Grid.RowSpacingProperty] = GuideParts.Space(8).ToBinding() };
        // Child minima alone do not constrain star-row allocation. Reserve the
        // comparison and editable-result rows so their controls cannot overflow
        // into the summary or choice buttons when the window fits its content.
        right.RowDefinitions[1].MinHeight = 180;
        right.RowDefinitions[4].MinHeight = 160;
        right.Children.Add(_summary);
        right.Children.Add(panes);
        right.Children.Add(_choices);
        right.Children.Add(new TextBlock { Text = "Merged result (editable)", FontWeight = FontWeight.SemiBold });
        right.Children.Add(_result);
        Grid.SetRow(panes, 1);
        Grid.SetRow(_choices, 2);
        Grid.SetRow(right.Children[3], 3);
        Grid.SetRow(_result, 4);

        var buttons = GuideParts.Actions(_accept, cancel);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(12).ToBinding() };
        _list.MaxHeight = 200;
        _differences.MaxHeight = 180;
        _differences[!TextBox.BackgroundProperty] = this.GetResourceObservable("Tern.WarningBackground").ToBinding();
        var conflicts = new StackPanel { [!StackPanel.SpacingProperty] = GuideParts.Space(8).ToBinding(), MaxWidth = 170, IsVisible = _conflicts.Count > 0, Children =
        { new WrapPanel { Children = { previous, next } }, _list, manual,
          new Expander { Header = "Line changes (+/−)", Content = _differences },
          new TextBlock { Text = savesOnAccept ? "Accept applies the merged text and saves it to the file." : "Accept puts the merged text in the editor and leaves it unsaved. Review it, then Save.", TextWrapping = TextWrapping.Wrap } } };
        body.Children.Add(new ScrollViewer { Content = conflicts, IsVisible = _conflicts.Count > 0 });
        body.Children.Add(right);
        Grid.SetColumn(right, 1);

        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("*,Auto"), [!Grid.RowSpacingProperty] = GuideParts.Space(12).ToBinding() };
        root.Children.Add(body);
        root.Children.Add(buttons);
        Grid.SetRow(buttons, 1);
        Content = root;

        _list.SelectionChanged += (_, _) => ShowSelected();
        _choices.IsVisible = _conflicts.Count > 0;
        _list.IsVisible = _conflicts.Count > 0;
        _list.SelectedIndex = _conflicts.Count > 0 ? 0 : -1;
        Rebuild();
        UpdateReview();
    }

    private static Grid LabeledPane(string label, Control source)
    {
        var pane = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), [!Grid.RowSpacingProperty] = GuideParts.Space(4).ToBinding() };
        pane.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        pane.Children.Add(source); Grid.SetRow(source, 1);
        return pane;
    }

    private void Step(int direction)
    {
        if (_conflicts.Count > 0) _list.SelectedIndex = (_list.SelectedIndex + direction + _conflicts.Count) % _conflicts.Count;
    }

    private void UpdateReview()
    {
        var selected = _list.SelectedIndex;
        _list.ItemsSource = _conflicts.Select((c, i) => $"Conflict {i + 1} · {_picks[c]}").ToArray();
        _list.SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, _conflicts.Count - 1));
        var reviewed = _picks.Values.Count(p => p != Pick.Unreviewed);
        _summary.Text = _conflicts.Count == 0 ? "No conflicts. Changes combined cleanly." : $"{reviewed} of {_conflicts.Count} conflicts reviewed. Unreviewed regions show provisional text.";
        _accept.IsEnabled = reviewed == _conflicts.Count;
    }

    private static string Difference(string original, string changed)
    {
        var before = LineSplitter.Split(original);
        var after = LineSplitter.Split(changed);
        var matches = LineDiff.MatchLines(before, after);
        var matched = matches.Where(i => i >= 0).ToHashSet();
        return string.Concat(before.Where((_, i) => matches[i] < 0).Select(l => "− " + l))
            + string.Concat(after.Where((_, i) => !matched.Contains(i)).Select(l => "+ " + l));
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
        _differences.Text = "Your edits\n" + Difference(conflict.Base, conflict.Mine) + "\nDisk changes\n" + Difference(conflict.Base, conflict.Disk);
    }

    private async Task ChooseAsync(Pick pick)
    {
        if (_result.Text != _generated && await PromptWindow.AskAsync(this, "Replace manual merge edits?",
            "Changing this choice rebuilds the merged result and replaces your manual edits. Cancel to keep your text.",
            ["Rebuild result", "Cancel"], 1, 1) != 0) return;
        if (_list.SelectedIndex >= 0)
        {
            foreach (var conflict in _conflicts.Where(c => _picks[c] == Pick.Manual)) _picks[conflict] = Pick.Unreviewed;
            _picks[_conflicts[_list.SelectedIndex]] = pick;
            Rebuild();
            UpdateReview();
        }
    }

    private void Rebuild() => _result.Text = _generated = _merge.Render(c => _picks[c] switch
    {
        Pick.Disk => c.Disk,
        Pick.Both => c.Both(_merge.NewLine),
        Pick.Base => c.Base,
        _ => c.Mine,
    });
}
