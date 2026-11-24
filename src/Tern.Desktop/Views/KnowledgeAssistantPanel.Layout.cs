using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Tern.App.Knowledge;
using Tern.App.Shell;
using Tern.Workspaces;

namespace Tern.Desktop.Views;

internal sealed partial class KnowledgeAssistantPanel
{
    private const int AskStage = 0, ChooseStage = 1, AnswerStage = 2, NotFoundStage = 3, ReportStage = 4;
    private static readonly (string Title, string Step, string Tip)[] Guides =
    [
        ("Ask your notes", "Step 1 of 3", "Tip: Use names, dates or project names. Choose a suggestion if you are not sure what to ask."),
        ("Check your notes", "Step 2 of 3", "Tip: AI ticked the notes that fit best. Untick anything private or unrelated, or add a note."),
        ("Your answer", "Step 3 of 3", "Tip: Read the answer before you share it. Open a note if you want to check a fact."),
        ("We could not find this in your notes", "Step 3 of 3", "Tip: Use a word you remember from the note, such as a name, date or project."),
        ("Check your report", "Last step", "Tip: Read it as your manager would. Change anything that is not right before you continue."),
    ];

    private static readonly (string Group, (string Title, string Question, string Task)[] Items)[] Starters =
    [
        ("FIND", [("A decision", "What did we decide about [topic]?", AnswerTask), ("A date", "When is [event or deadline]?", AnswerTask), ("Who is responsible", "Who is responsible for [task]?", AnswerTask)]),
        ("UNDERSTAND", [("Summarize", "Summarize what my notes say about [topic].", "Summarize notes"), ("Explain simply", "Explain [topic] in simple words.", "Explain topic"), ("Main risks", "What are the main risks for [project name]?", AnswerTask)]),
        ("PLAN", [("Open tasks", "What tasks are still open for [project name]?", "Extract action items"), ("Next steps", "What should happen next for [project name]?", AnswerTask), ("Compare options", "Compare [option A] and [option B].", "Compare concepts")]),
        ("WRITE", [("Update email", "Write a short update about [topic].", "Draft outline"), ("Meeting summary", "Summarize the meeting about [topic].", "Summarize notes"), ("Key facts", "List the key facts about [topic].", AnswerTask)]),
    ];

    private readonly TextBlock _title = GuideParts.Title("");
    private readonly TextBlock _usingAI = GuideParts.Caption("");
    private readonly TextBlock _journey = GuideParts.Caption("");
    private readonly TextBlock _tip = GuideParts.Body("");
    private readonly Grid _pages = new();
    private readonly MessageBar _askBar = new();
    private readonly MessageBar _chooseBar = new();
    private readonly TextBlock _selectedCount = GuideParts.Caption("");
    private readonly TextBlock _chooseTitle = GuideParts.Body("");
    private readonly TextBlock _reviewQuestion = GuideParts.Caption("");
    private readonly Button _back = new() { Content = "Back" };
    private readonly Button _allowOnline = new() { Content = "Yes, use online AI", IsVisible = false };
    private readonly Button _selectAll = new() { Content = "Select all" };
    private readonly Button _selectNone = new() { Content = "Select none" };
    private readonly Button _showMore = new() { IsVisible = false };
    private readonly Button _addNote = new() { Content = "Add a note" };
    private readonly Button _finishReading = new() { Content = "Finish reading my notes", IsVisible = false };
    private readonly MessageBar _coverageBar = new();
    private readonly Button _newQuestion = new() { Content = "Ask a new question" };
    private readonly Button _otherFolder = new() { Content = "Search a different folder" };
    private readonly Button _searchAnyway = new() { Content = "Search with my words" };
    private readonly StackPanel _noteRows = new() { Spacing = 6 };
    private readonly StackPanel _moreRows = new() { Spacing = 6, IsVisible = false };
    private readonly StackPanel _answerLines = new() { Spacing = 6 };
    private readonly WrapPanel _foundIn = new();
    private readonly StackPanel _relatedRows = new() { Spacing = 6 };
    private readonly TextBlock _notFoundReason = GuideParts.Body("");
    private readonly TextBlock _notFoundAdvice = GuideParts.Body("");
    private readonly StackPanel _notFoundSuggestions = new() { Spacing = 6 };
    private readonly StackPanel _askSuggestions = new() { Spacing = 6 };
    private readonly ContentControl _loading = new();
    private readonly TextBlock _nextTitle = GuideParts.Body("");
    private readonly TextBlock _nextBody = GuideParts.Caption("");
    private readonly Button _nextAction = new() { IsVisible = false };
    private readonly List<Button> _starters = [];
    private readonly List<Button> _suggestionButtons = [];
    private Border _nextCard = null!;
    private Border _askSuggestionsArea = null!;
    private Control _notFoundSuggestionsArea = null!;
    private Control _relatedArea = null!;
    private NextAction _nextActionKind;
    private bool _nextActionRunning;
    private int _stage;

    private void BuildLayout()
    {
        _scope.HorizontalAlignment = HorizontalAlignment.Stretch;
        var help = new Button { Content = "How it works" };
        NameControl(_loading, "Progress"); NameControl(_usingAI, "AI in use");
        NameControl(_journey, "Step"); NameControl(_title, "Screen title");
        NameControl(_tip, "Tip"); NameControl(_askBar.Label, "Help for your question"); NameControl(_chooseBar.Label, "Help for choosing notes");
        NameControl(_allowOnline, "Yes, use online AI"); NameControl(_selectAll, "Select all");
        NameControl(_selectNone, "Select none"); NameControl(_showMore, "Show more notes");
        NameControl(_newQuestion, "Ask a new question"); NameControl(_back, "Back");
        NameControl(_otherFolder, "Search a different folder");
        NameControl(_done, "Done with this task"); NameControl(_retry, "Retry interrupted step"); NameControl(_refreshNotes, "Refresh notes");
        ToolTip.SetTip(_done, "Finish this task and clear its saved question, choices and draft. Copied or exported files stay where you put them.");
        ToolTip.SetShowOnDisabled(_done, true);
        _done.Click += (_, _) => DoneTask();
        _retry.Click += async (_, _) => await RetryTaskAsync();
        _refreshNotes.Click += async (_, _) => { if (_work is null) await RunAsync(RefreshTaskNotesAsync); };
        ToolTip.SetTip(_usingAI, "To change the AI, use the AI button at the top of Tern.");
        var helpContent = GuideParts.Vertical(GuideParts.Title("How it works"),
            GuideParts.Body("1. Ask: type what you want to know, or choose a suggestion."),
            GuideParts.Body("2. Check: AI ticks the notes that fit. Untick any, or add a note. AI reads only the ticked notes."),
            GuideParts.Body("3. Answer: read the answer, then copy it, save a PDF, add it to a note or print it."),
            GuideParts.Caption("To change the AI, use the AI button at the top of Tern. Your notes never change unless you choose Add to my note."));
        helpContent.MaxWidth = 460;
        var helpFlyout = DialogParts.PanelFlyout(GuideParts.Card(helpContent));
        help.Click += (_, _) => helpFlyout.ShowAt(help);

        var heading = new Grid { ColumnDefinitions = new("*,Auto") };
        heading.Children.Add(_title);
        Grid.SetColumn(help, 1); heading.Children.Add(help);
        var progress = new Grid { ColumnDefinitions = new("*,Auto") };
        progress.Children.Add(_journey);
        Grid.SetColumn(_usingAI, 1); progress.Children.Add(_usingAI);
        var tipCard = GuideParts.Card(_tip);
        NameControl(tipCard, "Screen guide");
        var header = GuideParts.Vertical(heading, progress, tipCard);

        var ask = BuildAskPage();
        var choose = BuildChoosePage();
        var answer = BuildAnswerPage();
        var notFound = BuildNotFoundPage();
        var report = BuildReportPage();
        foreach (var page in new[] { ask, choose, answer, notFound, report })
        {
            page.Children[0].Focusable = true;
            page.Bind(MarginProperty, page.GetResourceObservable("Tern.Space.12", value => new Thickness(0, 0, value is double gap ? gap : 12, 0)));
            _pages.Children.Add(new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        }
        _back.Click += (_, _) => GoBack();
        _newQuestion.Click += (_, _) => { if (_work is null) { DoneTask(); _question.Focus(); } };
        _otherFolder.Click += (_, _) => { if (_work is null) { ShowStage(AskStage); _scope.Focus(); _scope.IsDropDownOpen = true; } };
        _allowOnline.Click += (_, _) =>
        {
            _shell.Settings.KnowledgeAllowRemote = true;
            if (!_shell.SaveSettings()) _status.Text = "Online AI is on for now, but Tern must ask again next time.";
            RefreshPrepared();
        };
        var footer = new StackPanel { Spacing = 6 };
        footer.Children.Add(_askBar); footer.Children.Add(_chooseBar); footer.Children.Add(_reportBar);
        footer.Children.Add(GuideParts.ButtonRow(_allowOnline, _configure, _openSettings, _back, _otherFolder, _newQuestion, _find, _send, _shareRun, _reportRun, _refreshNotes, _retry, _done, _cancel));
        footer.Children.Add(_taskBar);
        footer.Children.Add(_loading); footer.Children.Add(_statusBar);
        var frame = GuideParts.Frame(header, _pages, footer);
        var viewport = new ScrollViewer { Content = frame, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        viewport.AddHandler(RequestBringIntoViewEvent, (_, e) =>
        {
            // An inner page or text box can handle this first, so expose the final target in both viewports.
            var target = e.TargetObject;
            var rectangle = e.TargetRect;
            var stage = _stage;
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || stage != _stage) return;
                viewport.UpdateLayout();
                if (target.TranslatePoint(rectangle.Position, viewport) is not { } point) return;
                var offset = viewport.Offset.Y;
                if (point.Y < 0) offset += point.Y;
                else if (point.Y + Math.Min(rectangle.Height, viewport.Bounds.Height) > viewport.Bounds.Height)
                    offset += point.Y + Math.Min(rectangle.Height, viewport.Bounds.Height) - viewport.Bounds.Height;
                viewport.Offset = new Vector(viewport.Offset.X, Math.Max(0, offset));
            }, DispatcherPriority.Loaded);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        void Fit()
        {
            // Ten lines when there is room. Small windows and large text keep the buttons reachable.
            _question.MinLines = viewport.Bounds.Height >= GuideParts.Scaled(760) ? 10 : 3;
            // At large text scales the header and actions can be taller than the window.
            var chrome = header.Bounds.Height + footer.Bounds.Height + frame.RowSpacing * 2;
            frame.Height = Math.Max(viewport.Bounds.Height - frame.Margin.Top - frame.Margin.Bottom,
                Math.Max(GuideParts.Scaled(300), chrome + GuideParts.Scaled(96)));
        }
        viewport.SizeChanged += (_, _) => Fit(); _question.PropertyChanged += (_, e) => { if (e.Property == TextBox.FontSizeProperty) Fit(); };
        header.SizeChanged += (_, _) => Fit(); footer.SizeChanged += (_, _) => Fit();
        Child = viewport; Fit(); RefreshMotion(); ShowStage(AskStage);
    }

    private StackPanel BuildAskPage()
    {
        var label = GuideParts.Title("What do you want to know?");
        var scopeRow = new Grid { ColumnDefinitions = new("Auto,*"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(12).ToBinding() };
        var scopeLabel = GuideParts.Body("Search in"); scopeLabel.VerticalAlignment = VerticalAlignment.Center;
        scopeRow.Children.Add(scopeLabel); Grid.SetColumn(_scope, 1); scopeRow.Children.Add(_scope);
        NameControl(_nextTitle, "Next step"); NameControl(_nextBody, "Next step details"); NameControl(_nextAction, "Next step action");
        _nextAction.Click += async (_, _) => await RunNextActionAsync();
        _nextCard = GuideParts.Card(GuideParts.Vertical(_nextTitle, _nextBody, GuideParts.ButtonRow(_nextAction)));
        NameControl(_searchAnyway, "Search with my words");
        _searchAnyway.Click += async (_, _) => { ClearAskSuggestions(); await RunAsync(FindAsync); };
        _askSuggestionsArea = GuideParts.Card(GuideParts.Vertical(GuideParts.Body("This question is a little broad. Try one of these:"), _askSuggestions, GuideParts.ButtonRow(_searchAnyway)));
        NameControl(_askSuggestionsArea, "Clearer questions");
        _askSuggestionsArea.IsVisible = false;
        var columns = new Grid { ColumnDefinitions = new("*,*,*,*"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(8).ToBinding() };
        for (var column = 0; column < Starters.Length; column++)
        {
            var group = new StackPanel { Spacing = 6 };
            var caption = GuideParts.Caption(Starters[column].Group); caption.FontWeight = FontWeight.SemiBold;
            caption[!TextBlock.ForegroundProperty] = caption.GetResourceObservable(Tern.Desktop.Themes.ThemeManager.Accent).ToBinding();
            group.Children.Add(caption);
            foreach (var (title, question, task) in Starters[column].Items)
            {
                var starter = new Button { Content = title, Classes = { "tern-tile" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                NameControl(starter, "Suggestion: " + title);
                ToolTip.SetTip(starter, question);
                starter.Click += (_, _) => UseStarter(question, task);
                group.Children.Add(starter);
                _starters.Add(starter);
            }
            var groupCard = GuideParts.Card(group);
            Grid.SetColumn(groupCard, column);
            columns.Children.Add(groupCard);
        }
        var page = GuideParts.Vertical(label, _question, scopeRow, _nextCard, _askSuggestionsArea, GuideParts.Body("Not sure what to ask? Choose one"), columns);
        NameControl(page, "Ask your notes");
        return page;
    }

    private void UseStarter(string question, string task)
    {
        if (_work is not null) return;
        _refreshing = true; _question.Text = question; _refreshing = false;
        _task = task; _starterPrefix = question[..question.IndexOf('[')]; _vagueChecked = null;
        ClearAskSuggestions();
        ClearSelection();
        _question.Focus();
        _question.SelectionStart = question.IndexOf('[');
        _question.SelectionEnd = question.IndexOf(']') + 1;
    }

    private StackPanel BuildChoosePage()
    {
        _chooseTitle.FontWeight = FontWeight.SemiBold;
        _selectedCount.VerticalAlignment = VerticalAlignment.Center;
        _selectAll.Click += (_, _) => SetAllChoices(true);
        _selectNone.Click += (_, _) => SetAllChoices(false);
        _showMore.Click += (_, _) => { _moreRows.IsVisible = true; _showMore.IsVisible = false; };
        var bar = new Grid { ColumnDefinitions = new("*,Auto") };
        bar.Children.Add(_selectedCount);
        var buttons = GuideParts.ButtonRow(_selectAll, _selectNone);
        Grid.SetColumn(buttons, 1); bar.Children.Add(buttons);
        NameControl(_addNote, "Add a note"); NameControl(_coverageBar.Label, "What Tern searched");
        ToolTip.SetTip(_addNote, "Choose another note from this folder.");
        _addNote.Click += async (_, _) => await RunAsync(AddNoteAsync);
        var page = GuideParts.Vertical(GuideParts.Title("Which notes should AI read?"), _chooseTitle, _reviewQuestion, _coverageBar, bar, _noteRows, _moreRows, GuideParts.ButtonRow(_showMore, _addNote),
            GuideParts.Caption("Only the ticked notes are read by AI. To choose them, AI saw the names and headings of possible notes."));
        NameControl(page, "Check your notes");
        return page;
    }

    private void SetAllChoices(bool selected)
    {
        _refreshing = true;
        foreach (var choice in _choices) { choice.Include.IsChecked = selected; _userChoices[choice.Source.Path] = selected; }
        _refreshing = false;
        if (selected && _moreRows.Children.Count > 0) { _moreRows.IsVisible = true; _showMore.IsVisible = false; }
        RefreshPrepared();
    }

    private (Control Row, CheckBox Include) NoteRow(WorkspaceSession root, KnowledgeSource source, bool ticked)
    {
        var include = new CheckBox { IsChecked = ticked, VerticalAlignment = VerticalAlignment.Top };
        NameControl(include, $"Include note: {source.RelativePath}");
        ToolTip.SetTip(include, source.RelativePath);
        include.IsCheckedChanged += (_, _) =>
        {
            if (_refreshing) return;
            _userChoices[source.Path] = include.IsChecked == true;
            RefreshPrepared();
            QueueCheckpoint();
        };
        var name = GuideParts.Body(Path.GetFileNameWithoutExtension(source.RelativePath));
        name.FontWeight = FontWeight.SemiBold;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;
        var sentence = GuideParts.Caption(source.WhyChosen is { } why ? "Why AI chose it: " + why : BestSentence(source.Excerpt, QuestionText));
        var open = new Button { Content = "View", VerticalAlignment = VerticalAlignment.Center };
        NameControl(open, $"View note: {source.RelativePath}");
        ToolTip.SetTip(open, "Open this note. Reopen Explore your notes to come back.");
        open.Click += async (_, _) => await RunAsync(async token => { await _retrieval.ValidateAsync(root, [source], Limits, token); await _open(source.Path, source.Line); });
        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), [!Grid.ColumnSpacingProperty] = GuideParts.Space(8).ToBinding() };
        var text = new StackPanel { Spacing = 2, Children = { name, sentence } };
        grid.Children.Add(include); Grid.SetColumn(text, 1); grid.Children.Add(text); Grid.SetColumn(open, 2); grid.Children.Add(open);
        return (GuideParts.Card(grid), include);
    }

    private static readonly Regex MarkupNoise = new(@"<[^>\n]+>|!\[[^\]]*\]\([^)]*\)|^\s*>?\s*\[![A-Z]+\]|^\s{0,3}#{1,6}\s+.*$|^\s*\|?[\s:|-]+\|?\s*$|\*\*|__|`|^\s*[-*+>]\s+|^\s*\d+\.\s+",
        RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex MarkdownLink = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>One clean sentence from a passage: the one that best fits the question.</summary>
    internal static string BestSentence(string markdown, string question, int limit = 130)
    {
        string text;
        try { text = MarkupNoise.Replace(MarkdownLink.Replace(markdown, "$1"), " ").Replace('|', ' '); }
        catch (RegexMatchTimeoutException) { text = markdown; }
        var words = Regex.Matches(question, @"\p{L}[\p{L}\p{N}-]{3,}", RegexOptions.None, TimeSpan.FromMilliseconds(100))
            .Select(match => KnowledgeRetrieval.StemForDisplay(match.Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var sentences = Regex.Split(text, @"(?<=[.!?])\s+|\r?\n", RegexOptions.None, TimeSpan.FromMilliseconds(100))
            .Select(part => string.Join(' ', part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).Where(part => part.Length >= 12).ToArray();
        var best = sentences.OrderByDescending(part => words.Count(word => part.Contains(word, StringComparison.OrdinalIgnoreCase))).FirstOrDefault()
            ?? string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return best.Length <= limit ? best : best[..limit].TrimEnd() + "…";
    }

    private StackPanel BuildNotFoundPage()
    {
        var tips = GuideParts.Vertical(GuideParts.Body("Try this:"), GuideParts.Caption("• Use a word that appears in your notes."),
            GuideParts.Caption("• Name a person, date or project."), GuideParts.Caption("• Search a different folder."));
        _notFoundSuggestionsArea = GuideParts.Card(GuideParts.Vertical(GuideParts.Body("You could ask:"), _notFoundSuggestions));
        NameControl(_notFoundSuggestionsArea, "Better questions");
        _notFoundSuggestionsArea.IsVisible = false;
        NameControl(_notFoundReason, "Why we could not find it");
        NameControl(_finishReading, "Finish reading my notes"); NameControl(_notFoundAdvice, "AI advice");
        _notFoundAdvice.IsVisible = false;
        _finishReading.Click += (_, _) =>
        {
            if (_work is not null) return;
            Scope?.RequestRescan();
            _finishReading.IsVisible = false;
            ShowStage(AskStage);
            _status.Text = "Getting the rest of your notes ready…";
        };
        var page = GuideParts.Vertical(GuideParts.Title("What would you like to try next?"), _notFoundReason, _notFoundAdvice, GuideParts.ButtonRow(_finishReading), _notFoundSuggestionsArea, GuideParts.Card(tips));
        NameControl(page, "Not found");
        return page;
    }

    private void SetSuggestions(StackPanel panel, IReadOnlyList<string> ideas)
    {
        foreach (var old in panel.Children.OfType<Button>()) _suggestionButtons.Remove(old);
        panel.Children.Clear();
        foreach (var idea in ideas)
        {
            var text = idea;
            var button = new Button { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            NameControl(button, "Use this question: " + text);
            button.Click += (_, _) => UseSuggestion(text);
            panel.Children.Add(button);
            _suggestionButtons.Add(button);
        }
    }

    private void ShowAskSuggestions(IReadOnlyList<string> ideas)
    {
        SetSuggestions(_askSuggestions, ideas);
        _askSuggestionsArea.IsVisible = true;
        if (ideas.Count == 0) _status.Text = "Add a name, date or topic to get a better result, or press Search with my words.";
    }

    private void ClearAskSuggestions()
    {
        SetSuggestions(_askSuggestions, []);
        _askSuggestionsArea.IsVisible = false;
    }

    private void GoBack()
    {
        if (_work is not null) return;
        CaptureReportEdits();
        switch (_stage)
        {
            case ReportStage: ShowStage(AnswerStage); break;
            case NotFoundStage when _choices.Count > 0: ShowStage(ChooseStage); RefreshPrepared(); break;
            default: ShowStage(AskStage); _question.Focus(); break;
        }
    }

    private void SetFolderHelp(string title, string body, string? action, NextAction kind)
    {
        _nextTitle.Text = title; _nextBody.Text = body; _nextActionKind = kind;
        _nextAction.Content = action; _nextAction.IsVisible = action is not null && (kind != NextAction.OpenFolder || _openFolder is not null);
        if (_nextAction.Parent is Control row) row.IsVisible = _nextAction.IsVisible;
        _nextAction.IsEnabled = _work is null && !_nextActionRunning;
    }

    private async Task RunNextActionAsync()
    {
        if (_nextActionRunning || _work is not null) return;
        var action = _nextActionKind;
        _nextActionRunning = true; RefreshAvailability();
        try
        {
            switch (action)
            {
                case NextAction.OpenFolder when _openFolder is not null: await _openFolder(); break;
                case NextAction.Resume: Scope?.RequestRescan(); _status.Text = "Getting the rest of your notes ready…"; break;
                case NextAction.Promote when Scope is { } root:
                    var path = root.Root;
                    var question = _question.Text;
                    var ready = false;
                    var opened = await _shell.PromoteFolderAsync(path, treeReady: () =>
                    {
                        ready = true;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_disposed) return;
                            RefreshWorkspaces();
                            var workspace = _shell.OpenWorkspaces.FirstOrDefault(item => WorkspacePaths.PathsEqual(item.Root, path));
                            if (workspace is null) return;
                            _scope.SelectedItem = workspace;
                            _question.Text = question;
                            _status.Text = "This folder is ready. Your notes are being prepared in the background.";
                            _question.Focus();
                        });
                    });
                    if (!opened && !ready && !_disposed) _status.Text = _shell.StatusMessage;
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (!_disposed) _status.Text = UserProblem.Explain(ex, "Could not finish that step").Text;
        }
        finally
        {
            _nextActionRunning = false;
            if (!_disposed) RefreshAvailability();
        }
    }

    private enum NextAction { None, OpenFolder, Promote, Resume }

    private void RefreshMotion()
    {
        foreach (var page in _pages.Children)
        {
            page.Transitions = Services.MotionPolicy.IsReduced ? null : new Transitions { new DoubleTransition
                { Property = OpacityProperty, Duration = Services.MotionPolicy.Duration(Services.MotionPurpose.Tab) } };
            page.Opacity = 1;
        }
    }

    private void ShowStage(int stage)
    {
        var changed = _stage != stage;
        _stage = stage;
        if (changed) { CheckConnection(); _printerChecked = false; }
        for (var index = 0; index < _pages.Children.Count; index++)
        {
            var page = _pages.Children[index]; page.IsVisible = index == stage;
            if (index == stage && changed && !Services.MotionPolicy.IsReduced)
            {
                page.Opacity = 0;
                Dispatcher.UIThread.Post(() => { if (!_disposed && _stage == stage) page.Opacity = 1; }, DispatcherPriority.Render);
            }
        }
        var guide = Guides[stage];
        _title.Text = guide.Title; _journey.Text = guide.Step; _tip.Text = guide.Tip;
        _find.IsVisible = stage == AskStage;
        _send.IsVisible = stage == ChooseStage;
        _shareRun.IsVisible = stage == AnswerStage;
        _reportRun.IsVisible = stage == ReportStage;
        _back.IsVisible = stage is ChooseStage or NotFoundStage or ReportStage;
        _back.Content = stage switch { ChooseStage => "Back", NotFoundStage => "Change my question", _ => "Back to answer" };
        if (stage == NotFoundStage && _choices.Count > 0) _back.Content = "Choose other notes";
        _otherFolder.IsVisible = stage == NotFoundStage;
        // On the not-found screen the Back button is the one main action. A second "ask again" button would repeat it.
        _newQuestion.IsVisible = stage == AnswerStage || (stage == NotFoundStage && _choices.Count > 0);
        foreach (var (button, active) in new[] { (_find, stage == AskStage), (_send, stage == ChooseStage), (_shareRun, stage == AnswerStage), (_reportRun, stage == ReportStage), (_back, stage == NotFoundStage) })
        { button.Classes.Set("accent", active); button.IsDefault = active; }
        RefreshAvailability();
        QueueCheckpoint();
        if (changed && stage > AskStage)
        {
            UpdateLayout();
            ((StackPanel)((ScrollViewer)_pages.Children[stage]).Content!).Children[0].Focus();
        }
    }
}
