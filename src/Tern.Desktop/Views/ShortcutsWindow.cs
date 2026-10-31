using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;
using Tern.App.Commands;
using Tern.App.Help;
using Tern.Desktop.Services;

namespace Tern.Desktop.Views;

/// <summary>Category browser, searchable command list and focused shortcut recording workspace.</summary>
public sealed class ShortcutsWindow : Window
{
    private readonly CommandRegistry _registry;
    private readonly ListBox _list = new();
    private readonly ListBox _categories = new();
    private readonly TextBox _capture = new() { PlaceholderText = "Record a shortcut", IsReadOnly = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _search = new() { PlaceholderText = "Find a command, category or feature…" };
    private readonly TextBlock _details = GuideParts.Body("Select a command to see its purpose and customize its keyboard shortcut.");
    private readonly TextBlock _selectedTitle = GuideParts.Title("Choose a command");
    private readonly TextBlock _binding = GuideParts.Caption("");
    private bool _recording;

    public ShortcutsWindow(CommandRegistry registry, Func<string, Task>? openFeature = null, Func<string, Task>? askAI = null,
        Func<string, CancellationToken, Task<string>>? answerAI = null)
    {
        _registry = registry;
        DialogParts.Setup(this, "Keyboard shortcuts", 1080, 740);
        MinWidth = 840;
        MinHeight = 520;
        AutomationProperties.SetName(_search, "Search keyboard shortcuts");
        AutomationProperties.SetName(_list, "Commands and shortcuts");
        AutomationProperties.SetName(_capture, "New shortcut");
        AutomationProperties.SetName(_categories, "Shortcut categories");
        AutomationProperties.SetName(_details, "Selected command details");
        AutomationProperties.SetName(_binding, "Current and default shortcut");
        _categories.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((category, _) =>
            new TextBlock { Text = category, TextWrapping = TextWrapping.Wrap });
        ScrollViewer.SetHorizontalScrollBarVisibility(_categories, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _categories.Styles.Add(new Avalonia.Styling.Style(s => s.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) }
        });
        _categories.ItemsSource = new[] { "All commands" }.Concat(registry.Commands.Select(c => c.Category).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)).ToArray();
        _categories.SelectedIndex = 0;
        _categories.SelectionChanged += (_, _) => Rebuild(null);
        _search.TextChanged += (_, _) => Rebuild(null);
        _list.SelectionChanged += (_, _) => ShowSelection();
        _message.Classes.Add("muted");

        var assign = new Button { Content = "Assign" };
        var remove = new Button { Content = "Remove" };
        var reset = new Button { Content = "Reset" };
        var resetAll = new Button { Content = "Reset all" };
        AutomationProperties.SetName(assign, "Assign shortcut");
        AutomationProperties.SetName(remove, "Remove shortcut");
        AutomationProperties.SetName(reset, "Reset shortcut to default");
        AutomationProperties.SetName(resetAll, "Reset all shortcuts");
        assign.Click += async (_, _) => await AssignAsync();
        remove.Click += (_, _) => Remove();
        reset.Click += async (_, _) => await ResetAsync(false);
        resetAll.Click += async (_, _) => await ResetAsync(true);
        var record = new Button { Content = "Record keys" };
        AutomationProperties.SetName(record, "Record shortcut keys");
        record.Click += (_, _) =>
        {
            if (_list.SelectedItem is not ShortcutRow) { _message.Text = "Select a command first."; return; }
            _recording = true;
            _capture.Text = string.Empty;
            _capture.Focus();
            _message.Text = "Press a shortcut. Escape cancels recording; Assign saves the recorded combination.";
        };
        var clear = new Button { Content = "Clear recording" };
        AutomationProperties.SetName(clear, "Clear recorded shortcut");
        clear.Click += (_, _) => { _recording = false; _capture.Text = string.Empty; _message.Text = "Recording cleared. The assigned shortcut has not changed."; };
        var help = new Button { Content = "Feature guide" };
        help.Click += async (_, _) => { if (_list.SelectedItem is ShortcutRow row) await new HelpWindow(registry, row.Id, openFeature, askAI, answerAI).ShowDialog(this); };
        _capture.AddHandler(KeyDownEvent, OnCaptureKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ShortcutRow>((row, _) =>
        {
            if (row is null) return new Grid();
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, Margin = new Thickness(2, 6) };
            grid.Children.Add(GuideParts.Vertical(new TextBlock { Text = row.Title, TextWrapping = TextWrapping.Wrap }, GuideParts.Caption(row.Category)));
            var gesture = GuideParts.Caption(row.Gesture);
            grid.Children.Add(gesture);
            Grid.SetColumn(gesture, 1);
            return grid;
        });
        var close = new Button { Content = "Done", IsDefault = true, MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right };
        close.Classes.Add("accent");
        AutomationProperties.SetName(close, "Close shortcuts");
        close.Click += (_, _) => Close();
        var editor = GuideParts.Card(GuideParts.Vertical(_selectedTitle, _details, _binding, _capture,
            new WrapPanel { Children = { record, clear } }, new WrapPanel { Children = { assign, remove, reset } }, help,
            GuideParts.Caption("Remove unbinds this command. Reset restores its default. If another command uses the keys, Tern asks before moving them.")));
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*,315"), ColumnSpacing = 18, Children = { _categories, _list, new ScrollViewer { Content = editor, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } } };
        Grid.SetColumn(body.Children[1], 1);
        Grid.SetColumn(body.Children[2], 2);
        var footer = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(resetAll, Dock.Left);
        footer.Children.Add(close);
        footer.Children.Add(resetAll);
        _message.Margin = new Thickness(16, 0);
        footer.Children.Add(_message);
        Content = new Grid { Margin = new Thickness(28), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 18, Children =
        {
            GuideParts.Vertical(GuideParts.Title("Keyboard shortcuts"), GuideParts.Body("Find a command, review what it does, then choose the keys that feel natural.")), _search, body, footer,
        }};
        var layout = (Grid)Content;
        for (var i = 0; i < layout.Children.Count; i++) Grid.SetRow(layout.Children[i], i);
        Rebuild(null);
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_capture.IsFocused) Close(); };
    }

    private void ShowSelection()
    {
        _recording = false;
        _capture.Text = string.Empty;
        if (_list.SelectedItem is not ShortcutRow row)
        {
            _selectedTitle.Text = "Choose a command";
            _details.Text = "Select a command to see its purpose and customize its keyboard shortcut.";
            _binding.Text = "";
            return;
        }
        var feature = FeatureCatalog.Search(_registry, null).Single(f => f.Id == row.Id);
        _selectedTitle.Text = row.Title;
        _details.Text = feature.Description + "\n\n" + feature.Prerequisite;
        var defaultGesture = Gesture.Describe(_registry.TryGet(row.Id, out var definition) ? definition.DefaultGesture : null);
        _binding.Text = $"Current: {row.Gesture}\nDefault: {(defaultGesture.Length > 0 ? defaultGesture : "Not assigned")}";
    }
    internal sealed record ShortcutRow(string Id, string Title, string Category, string Gesture);

    internal ListBox Commands => _list;

    internal string Message => _message.Text ?? string.Empty;

    private void Rebuild(string? select)
    {
        var rows = _registry.Commands
            .Where(c => _categories.SelectedIndex == 0 || c.Category == _categories.SelectedItem as string)
            .Where(c => ($"{c.Title} {c.Category}").Contains(_search.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ShortcutRow(c.Id, c.Title, c.Category, Gesture.Describe(_registry.GetGesture(c.Id)) is { Length: > 0 } g ? g : "—"))
            .ToList();
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.Find(r => r.Id == select) ?? rows.FirstOrDefault();
        ShowSelection();
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_recording) return;
        if (e.Key == Key.Escape)
        {
            _recording = false;
            _capture.Text = string.Empty;
            _message.Text = "Recording canceled. No shortcut changed.";
            e.Handled = true;
            return;
        }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        var m = e.KeyModifiers;
        _capture.Text = new Gesture(m.HasFlag(KeyModifiers.Control), m.HasFlag(KeyModifiers.Alt), m.HasFlag(KeyModifiers.Shift), m.HasFlag(KeyModifiers.Meta), e.Key.ToString()).ToString();
        _recording = false;
        _message.Text = "Recorded " + Gesture.Describe(_capture.Text) + ". Choose Assign to use it, or Clear recording to cancel.";
        e.Handled = true;
    }

    private async Task AssignAsync()
    {
        if (_list.SelectedItem is ShortcutRow row && !string.IsNullOrWhiteSpace(_capture.Text))
        {
            await TryAssignAsync(row.Id, _capture.Text);
        }
    }

    /// <summary>Binds <paramref name="gesture"/> to a command; if another command owns it, asks before moving it over.</summary>
    internal async Task<bool> TryAssignAsync(string commandId, string gesture)
    {
        var result = _registry.SetBinding(commandId, gesture);
        if (result.Succeeded)
        {
            _message.Text = "Shortcut assigned.";
            Rebuild(commandId);
            return true;
        }

        if (result.Problem == BindingProblem.Conflict && result.ConflictingCommandId is { } other)
        {
            var otherTitle = _registry.TryGet(other, out var definition) ? definition.Title : other;
            var answer = await PromptWindow.AskAsync(this, "Shortcut already in use", $"{gesture} is used by “{otherTitle}”. Move it to this command?", ["Move shortcut", "Cancel"], 1, 1);
            if (answer == 0)
            {
                _registry.ForceBinding(commandId, gesture);
                _message.Text = $"Moved from “{otherTitle}”.";
                Rebuild(commandId);
                return true;
            }

            return false;
        }

        _message.Text = "That is not a valid shortcut.";
        return false;
    }

    private void Remove()
    {
        if (_list.SelectedItem is ShortcutRow row)
        {
            _registry.ClearBinding(row.Id);
            Rebuild(row.Id);
        }
    }

    private async Task ResetAsync(bool all)
    {
        if (all)
        {
            _registry.ResetAll();
            Rebuild(null);
        }
        else if (_list.SelectedItem is ShortcutRow row)
        {
            if (_registry.TryGet(row.Id, out var definition) && definition.DefaultGesture is { } gesture
                && !await TryAssignAsync(row.Id, gesture)) return;
            _registry.ResetBinding(row.Id);
            Rebuild(row.Id);
        }
    }
}
