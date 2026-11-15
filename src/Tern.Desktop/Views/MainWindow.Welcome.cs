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
        panel.Children.Add(GuideParts.Body("Start with a new note, or open an existing file or folder. Write in Source and switch to Reading to see the formatted document."));
        var choices = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        foreach (var (label, id) in new[] { ("New note", CommandIds.NewFile), ("Open files…", CommandIds.OpenFile), ("Open folder…", CommandIds.OpenFolder) })
        {
            var button = new Button { Content = label, MinWidth = 130, MinHeight = 38 };
            if (id == CommandIds.NewFile) button.Classes.Add("accent");
            button.Click += async (_, _) => { _welcomeIsOpen = false; HideOverlay(); await RunCommandAsync(id); UpdateChrome(); };
            choices.Children.Add(button);
        }
        panel.Children.Add(choices);
        panel.Children.Add(GuideParts.Card(GuideParts.Vertical(
            GuideParts.Caption("1. Write in Source → 2. Choose Reading → 3. Save with Ctrl+S"), new TextBlock { Text = "# A heading\n\n**Important words**", TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "A heading · Important words", FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap })));
        var sample = new Button { Content = "Try a sample note" };
        sample.Click += (_, _) => OpenStarter("Practice note.md", BeginnerPractice.Text);
        var templates = new Button { Content = "Starter templates" };
        var menu = new MenuFlyout();
        foreach (var (name, text) in new[]
        {
            ("Meeting notes", "# Meeting notes\n\n## Attendees\n\n- \n\n## Decisions\n\n- \n\n## Action items\n\n- [ ] \n"),
            ("README", "# Project name\n\nDescribe the project.\n\n## Getting started\n\n## Usage\n\n## License\n"),
            ("Checklist", "# Checklist\n\n- [ ] First task\n- [ ] Second task\n")
        })
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => OpenStarter(name + ".md", text);
            menu.Items.Add(item);
        }
        templates.Flyout = menu;
        var guide = new Button { Content = "Open guide" };
        guide.Click += async (_, _) => { HideOverlay(); await RunCommandAsync(CommandIds.Help); };
        panel.Children.Add(GuideParts.Caption("New to Markdown? Try the sample note to practise without changing your files, or open the guide for step-by-step help."));
        panel.Children.Add(new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { sample, templates, guide } });
        var dismiss = new Button { Content = "Dismiss first-note checklist", Classes = { "flat" } };
        dismiss.Click += (_, _) => DismissOnboarding();
        panel.Children.Add(dismiss);
        return panel;
    }

    private void OpenStarter(string name, string text)
    {
        _welcomeIsOpen = false;
        _onboardingSession = true;
        HideOverlay();
        var tab = _shell.NewDocument(text, standalone: true);
        tab.Session.SetDraftName(name);
        UpdateChrome();
    }

    private void ReplayWelcome()
    {
        _shell.Settings.OnboardingDismissed = false;
        _onboardingSession = true;
        _shell.SaveSettings();
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => HideOverlay();
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
        _shell.SaveSettings();
        HideOverlay();
        UpdateChrome();
    }

    private void OnDismissChecklistClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => DismissOnboarding();

    private void UpdateFirstNoteChecklist()
    {
        FirstNoteChecklist.IsVisible = _onboardingSession && !_shell.Settings.OnboardingDismissed && _shown is not null && !_welcomeIsOpen;
        if (_shown is not { } tab) return;
        if (_shell.ViewMode != Tern.App.Settings.ViewMode.Source && ReferenceEquals(_renderedPreviewTab, tab) && tab.Session.IsCurrent(_renderedPreviewRevision))
            _previewedNotes.Add(tab.Session.Id);
        FirstHeadingStep.Text = (tab.Analysis?.Headings.Count > 0 ? "✓ " : "○ ") + "Write a heading";
        FirstPreviewStep.Text = (_previewedNotes.Contains(tab.Session.Id) ? "✓ " : "○ ") + "Preview your note";
        FirstSaveStep.Text = (!tab.IsUntitled && !tab.IsDirty ? "✓ " : "○ ") + "Save your note";
    }
}
