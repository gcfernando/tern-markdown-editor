using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tern.App.Commands;
using Tern.App.Help;
using Avalonia.VisualTree;

namespace Tern.Desktop.Views;

public partial class MainWindow
{
    private bool _welcomeIsOpen;
    private bool _replayingWelcome;
    private bool _onboardingSession;
    private readonly HashSet<Guid> _previewedNotes = [];

    internal void ShowFirstUse()
    {
        if (_shell.Settings.OnboardingDismissed || _shell.Tabs.Count > 1 || _shown is not { IsUntitled: true } tab || tab.CurrentText.Length > 0) return;
        _welcomeIsOpen = _onboardingSession = true;
        UpdateChrome();
        UpdateLayout();
        EmptyText.GetVisualDescendants().OfType<Button>().FirstOrDefault()?.Focus();
    }

    private void ConfigureWelcome()
    {
        foreach (var child in WelcomeContent().Children.ToArray())
        {
            ((Panel)child.Parent!).Children.Remove(child);
            EmptyText.Children.Add(child);
        }
    }

    private StackPanel WelcomeContent()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(GuideParts.Title("Your writing space"));
        panel.Children.Add(GuideParts.Body("Start a note or open your files. Write in Source, then switch to Reading."));
        var choices = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        foreach (var (label, id) in new[] { ("New note", CommandIds.NewFile), ("Open files…", CommandIds.OpenFile), ("Open folder…", CommandIds.OpenFolder) })
        {
            var button = new Button { Content = label, MinWidth = 130, MinHeight = 38 };
            if (id == CommandIds.NewFile) button.Classes.Add("accent");
            button.Click += async (_, _) => { _welcomeIsOpen = false; HideOverlay(); await RunCommandAsync(id); UpdateChrome(); };
            choices.Children.Add(button);
        }
        panel.Children.Add(choices);
        var example = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 24 };
        var sourceHeading = new TextBlock { Text = "# A heading", FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"), TextWrapping = TextWrapping.Wrap };
        sourceHeading[!TextBlock.ForegroundProperty] = this.GetResourceObservable("Tern.Accent").ToBinding();
        example.Children.Add(GuideParts.Vertical(sourceHeading, new TextBlock { Text = "**Important words**", FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"), TextWrapping = TextWrapping.Wrap }));
        var rendered = GuideParts.Vertical(new TextBlock { Text = "A heading", FontSize = GuideParts.Scaled(18), FontWeight = FontWeight.SemiBold }, new TextBlock { Text = "Important words", FontWeight = FontWeight.Bold });
        Grid.SetColumn(rendered, 1); example.Children.Add(rendered);
        var teaching = GuideParts.Card(GuideParts.Vertical(GuideParts.Caption("1. Write in Source → 2. Choose Reading → 3. Save with Ctrl+S"), example));
        teaching.Tag = "quick-start-teaching";
        panel.Children.Add(teaching);
        var sample = new Button { Content = "Try a sample note" };
        sample.Click += (_, _) => OpenStarter("Practice note.md", BeginnerPractice.Text);
        var templates = new Button { Content = "Starter templates" };
        templates.Click += async (_, _) =>
        {
            if (await new StarterTemplatesWindow().ShowDialog<StarterTemplate?>(this) is { } item)
                OpenStarter(item.Name + ".md", item.Markdown);
        };
        var guide = new Button { Content = "Open guide" };
        guide.Click += async (_, _) => { HideOverlay(); await RunCommandAsync(CommandIds.Help); };
        panel.Children.Add(GuideParts.Caption("A little help getting started"));
        var reopen = new Button { Content = "Tern Quick Start…", Tag = "quick-start-reopen", IsVisible = _shell.Settings.OnboardingDismissed && !_replayingWelcome };
        reopen.Click += (_, _) => ReplayWelcome();
        panel.Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { sample, templates, guide, reopen } });
        var divider = new Border { Height = 1, Margin = new Thickness(0, 8), Tag = "quick-start-teaching" };
        divider[!Border.BackgroundProperty] = this.GetResourceObservable("Tern.Border").ToBinding();
        panel.Children.Add(divider);
        var dismiss = new Button { Content = "Hide Quick Start tips", Tag = "quick-start-teaching" };
        dismiss.Click += (_, _) => DismissOnboarding();
        panel.Children.Add(dismiss);
        return panel;
    }

    private void OpenStarter(string name, string text)
    {
        _welcomeIsOpen = false;
        _onboardingSession = true;
        HideOverlay();
        var tab = _shell.NewDocument(text.ReplaceLineEndings("\r\n"), standalone: true);
        tab.Session.SetDraftName(name);
        UpdateChrome();
    }

    private void ReplayWelcome()
    {
        _onboardingSession = true;
        _replayingWelcome = true;
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => { _replayingWelcome = false; HideOverlay(); UpdateFirstNoteChecklist(); };
        ShowOverlay(new Border { MaxWidth = 780, MaxHeight = Math.Max(180, Bounds.Height - 80),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Classes = { "card" }, Padding = new Thickness(28),
            Child = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = GuideParts.Vertical(WelcomeContent(), close) } });
        UpdateLayout();
        OverlayLayer.GetVisualDescendants().OfType<Button>().FirstOrDefault()?.Focus();
        UpdateFirstNoteChecklist();
    }

    private void DismissOnboarding()
    {
        _shell.Settings.OnboardingDismissed = true;
        _welcomeIsOpen = _onboardingSession = false;
        _replayingWelcome = false;
        HideOverlay();
        UpdateChrome();
        try { _shell.SaveSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _shell.StatusMessage = "Checklist hidden for this session. Its preference could not be saved: " + ex.Message; }
    }

    private void OnDismissChecklistClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => DismissOnboarding();

    private void UpdateFirstNoteChecklist()
    {
        FirstNoteChecklist.IsVisible = _onboardingSession && (!_shell.Settings.OnboardingDismissed || _replayingWelcome) && _shown is not null && !_welcomeIsOpen;
        if (_shown is not { } tab) return;
        if (_shell.ViewMode != Tern.App.Settings.ViewMode.Source && ReferenceEquals(_renderedPreviewTab, tab) && tab.Session.IsCurrent(_renderedPreviewRevision))
            _previewedNotes.Add(tab.Session.Id);
        FirstHeadingStep.Text = (tab.Analysis?.Headings.Count > 0 ? "✓ " : "○ ") + "Write a heading";
        FirstPreviewStep.Text = (_previewedNotes.Contains(tab.Session.Id) ? "✓ " : "○ ") + "Preview your note";
        FirstSaveStep.Text = (!tab.IsUntitled && !tab.IsDirty ? "✓ " : "○ ") + "Save your note";
    }
    private void SetWelcomeDetailsVisibility()
    {
        // Dismiss only the teaching. Samples, templates and help remain useful
        // after dismissal, including in an empty workspace after a restart.
        var showDetails = !_shell.Settings.OnboardingDismissed || _replayingWelcome;
        foreach (var control in EmptyText.Children)
            control.IsVisible = control.Tag is "quick-start-teaching" ? showDetails : true;
        foreach (var control in EmptyText.GetVisualDescendants().OfType<Button>().Where(control => control.Tag is "quick-start-reopen"))
            control.IsVisible = !showDetails;
    }

}
