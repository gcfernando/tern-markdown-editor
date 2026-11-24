using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Tern.App.Commands;
using Tern.App.Help;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private Action? _endWalkthrough;

    private Task BeginWalkthroughAsync(BeginnerLesson lesson)
    {
        _endWalkthrough?.Invoke();
        var practice = _shell.NewDocument(lesson.Example);
        practice.Session.SetDraftName("Practice · " + lesson.Name);
        var workspace = _shell.Workspace;
        var stepIndex = 0; var paused = false; var finished = false;
        var title = GuideParts.Title(lesson.Name);
        var instruction = GuideParts.Body("");
        var status = GuideParts.Caption("Practice note · unsaved. Other notes stay open.");
        var checkedStep = new CheckBox { Content = "I've checked this result in my practice note" };
        var next = new Button { Content = "Next" }; var back = new Button { Content = "Back" };
        var pause = new Button { Content = "Pause" }; var exit = new Button { Content = "Exit walkthrough" };
        var reveal = new Button { Content = "Show this tool" }; var replay = new Button { Content = "Replay", IsVisible = false };
        var guides = new Button { Content = "Return to guides", IsVisible = false };
        var popup = new Popup { IsLightDismissEnabled = false, Placement = PlacementMode.Bottom, Child = new Border
        { Classes = { "card" }, Padding = new Thickness(16), MaxWidth = 420, Child = GuideParts.Vertical(title, instruction, status, checkedStep, GuideParts.ButtonRow(reveal, back, next, pause, replay, guides, exit)) } };
        ((Grid)Content!).Children.Add(popup);
        string TargetCommand() => lesson.Id switch
        {
            "write" => stepIndex switch { 0 => CommandIds.NewFile, 1 => CommandIds.ViewSplit, 2 => CommandIds.ViewSource, _ => CommandIds.Save },
            "practice" => CommandIds.ViewSplit,
            "open" => CommandIds.OpenFile,
            _ => lesson.LaunchCommandId is { } command && FeatureCatalog.CanLaunchFromGuide(command) ? command : CommandIds.ViewSplit
        };
        Control? Target() => this.GetLogicalDescendants().OfType<Control>().FirstOrDefault(c => Equals(c.Tag, TargetCommand()) && c.IsEffectivelyVisible)
            ?? (TargetCommand() is CommandIds.ViewSource or CommandIds.ViewSplit or CommandIds.Save or CommandIds.NewFile ? SplitButton : null);
        void Show()
        {
            if (_windowClosed) return;
            popup.IsOpen = false;
            var target = Target();
            popup.PlacementTarget = target ?? SplitButton;
            title.Text = finished ? "Practice recap · " + lesson.Name : $"{lesson.Name} · {stepIndex + 1}/{lesson.Steps.Count}";
            instruction.Text = finished ? lesson.Result + "\nYour practice note is still open and unsaved. Replay or return to the related guide."
                : paused ? "Walkthrough paused. Resume returns to the captured practice note before continuing."
                : lesson.Steps[stepIndex].Title + "\n" + lesson.Steps[stepIndex].Action + "\nCheck: " + lesson.Steps[stepIndex].Expected;
            if (target is null) status.Text = "This target is hidden or unavailable. Show this tool uses the named command; no guessed location is highlighted.";
            checkedStep.IsVisible = !finished && !paused;
            next.IsEnabled = !finished && !paused && checkedStep.IsChecked == true;
            back.IsEnabled = !finished && !paused && stepIndex > 0;
            reveal.IsVisible = !finished && !paused;
            reveal.IsEnabled = CanRunCommand(TargetCommand());
            pause.Content = paused ? "Resume practice note" : "Pause";
            replay.IsVisible = finished;
            guides.IsVisible = finished;
            popup.IsOpen = true;
        }
        void End()
        {
            popup.IsOpen = false; ((Grid)Content!).Children.Remove(popup);
            _shell.PropertyChanged -= Changed;
            _endWalkthrough = null; FocusVisibleContent();
        }
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!ReferenceEquals(_shell.ActiveTab, practice) || !_shell.Tabs.Contains(practice) || !ReferenceEquals(workspace, _shell.Workspace))
            { paused = true; checkedStep.IsChecked = false; Show(); }
        }
        checkedStep.IsCheckedChanged += (_, _) => next.IsEnabled = !paused && checkedStep.IsChecked == true;
        next.Click += (_, _) => { if (!next.IsEnabled) return; if (++stepIndex >= lesson.Steps.Count) { stepIndex = lesson.Steps.Count - 1; finished = true; } checkedStep.IsChecked = false; Show(); };
        back.Click += (_, _) => { stepIndex = Math.Max(0, stepIndex - 1); checkedStep.IsChecked = false; Show(); };
        pause.Click += (_, _) =>
        {
            if (paused)
            {
                if (!_shell.Tabs.Contains(practice)) { status.Text = "The practice note was closed. Exit and replay from the guide to start a new practice note."; return; }
                _shell.ActiveTab = practice; workspace = _shell.Workspace; paused = false;
            }
            else paused = true;
            Show();
        };
        reveal.Click += async (_, _) => { if (CanRunCommand(TargetCommand())) await RunCommandAsync(TargetCommand()); Show(); };
        replay.Click += (_, _) => { stepIndex = 0; finished = false; paused = !ReferenceEquals(_shown, practice); checkedStep.IsChecked = false; Show(); };
        exit.Click += (_, _) => End();
        guides.Click += (_, _) => { End(); HelpWindow.ShowFor(this, () => CreateHelpWindow()); };
        popup.KeyDown += (_, e) => { if (e.Key == Key.Escape) { End(); e.Handled = true; } };
        _shell.PropertyChanged += Changed; _endWalkthrough = End;
        Activate(); Show(); exit.Focus();
        return Task.CompletedTask;
    }
}
