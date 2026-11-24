using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tern.App;
using Tern.App.Knowledge;
using Tern.App.Shell;
using Tern.Desktop.Services;
using Tern.Export;

namespace Tern.Desktop.Views;

internal sealed partial class KnowledgeAssistantPanel
{
    private const string ShareGroup = "tern-knowledge-share";
    private readonly RadioButton _shareCopy = new() { GroupName = ShareGroup, IsChecked = true, Classes = { "tern-choice" } };
    private readonly RadioButton _sharePdf = new() { GroupName = ShareGroup, Classes = { "tern-choice" } };
    private readonly RadioButton _shareNote = new() { GroupName = ShareGroup, Classes = { "tern-choice" } };
    private readonly RadioButton _sharePrint = new() { GroupName = ShareGroup, Classes = { "tern-choice" } };
    private readonly TextBlock _sharePrintTitle = GuideParts.Body("Print");
    private readonly MessageBar _sharePrintHelp = new();
    private readonly Button _shareRun = new() { Content = "Copy for email" };
    private readonly MessageBar _shareBar = new();
    private (MessageTone Tone, string Text)? _shareResult;
    private readonly Button _reportRun = new() { Content = "Save PDF" };
    private readonly MessageBar _reportBar = new();
    private readonly TextBox _reportTitle = new();
    private readonly TextBox _reportBy = new() { PlaceholderText = "Your name" };
    private readonly TextBox _reportHeadline = new() { AcceptsReturn = false, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _reportRecommendation = new() { AcceptsReturn = false, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _reportHeadlineLabel = GuideParts.Caption("");
    private readonly TextBlock _reportRecommendationLabel = GuideParts.Caption("");
    private readonly TextBox _reportFindings = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinLines = 3, MaxLines = 8, PlaceholderText = "One finding on each line" };
    private readonly TextBox _reportConditions = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinLines = 2, MaxLines = 6 };
    private readonly TextBox _reportRisks = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinLines = 2, MaxLines = 6 };
    private readonly TextBox _reportNextAction = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinLines = 2, MaxLines = 6 };
    private readonly TextBox _reportFinalAssessment = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinLines = 2, MaxLines = 6 };
    private readonly Button _reportPreview = new() { Content = "Preview report" };
    private readonly Expander _reportIssueDetails = new() { Header = "What needs checking", IsExpanded = false, IsVisible = false };
    private bool _reportLoaded, _loadingReport;
    private long _reportEditRevision;
    private readonly TextBlock _reportFindingsLabel = GuideParts.Caption("");
    private readonly TextBlock _reportIncludes = GuideParts.Caption("");
    private readonly StackPanel _reportMissing = new() { Spacing = 3 };
    private readonly MessageBar _reportNumbers = new();
    private readonly MessageBar _reportPartial = new();
    private readonly MessageBar _reportCheck = new();
    private string _reportMaterial = "";
    private string? _reportCheckedKey;
    private readonly TextBlock _reportPrinter = GuideParts.Body("");
    private readonly CheckBox _reportReviewed = new() { Content = "I reviewed this report" };
    private Control _reportMissingArea = null!;
    private KnowledgeReportDraft? _draft;
    private KnowledgeTurn? _draftTurn;
    private string _reportCoverage = "";

    /// <summary>Test seams: real runs use the clipboard, file picker and Windows default printer.</summary>
    internal Func<string?> DefaultPrinterName
    {
        get => _defaultPrinterName;
        set { _defaultPrinterName = value; _printerChecked = false; }
    }
    private Func<string?> _defaultPrinterName = () => OperatingSystem.IsWindows() ? WindowsTextPrinter.DefaultPrinterName() : null;
    private string? _printerName;
    private bool _printerChecked;
    internal Func<string, byte[], CancellationToken, Task> PrintPdf { get; set; } = (title, pdf, token) =>
        OperatingSystem.IsWindows() ? WindowsTextPrinter.PrintPdfToDefaultAsync(title, pdf, token) : throw new PlatformNotSupportedException("Printing needs Windows.");
    internal Func<string, Task<string?>>? PickPdfPath { get; set; }
    internal Func<string, Task>? CopyText { get; set; }
    internal Func<string, Task<string?>>? PickNote { get; set; }
    internal string? LatestQuestion => _conversation.Turns.Count > 0 ? _conversation.Turns[^1].Question : null;

    private StackPanel BuildAnswerPage()
    {
        NameControl(_shareCopy, "Copy for email"); NameControl(_sharePdf, "Save as PDF"); NameControl(_shareNote, "Add to my note");
        NameControl(_sharePrint, "Print"); NameControl(_shareRun, "Share this answer"); NameControl(_shareBar.Label, "Share status");
        NameControl(_answerLines, "Answer");
        foreach (var option in new[] { _shareCopy, _sharePdf, _shareNote, _sharePrint }) option.IsCheckedChanged += (_, _) => { _shareResult = null; if (_work is null) _status.Text = ""; RefreshShare(); SynchronizeReportOutputs(); RefreshReportAvailability(); QueueCheckpoint(); };
        _shareRun.Click += async (_, _) => await RunAsync(ShareAsync);
        _foundIn[!WrapPanel.ItemSpacingProperty] = GuideParts.Space(8).ToBinding();
        _foundIn[!WrapPanel.LineSpacingProperty] = GuideParts.Space(8).ToBinding();
        var answerCard = GuideParts.Card(GuideParts.Vertical(_answerLines, GuideParts.Caption("Found in"), _foundIn));
        var relatedTitle = GuideParts.Body("You may also want to read"); relatedTitle.FontWeight = FontWeight.SemiBold;
        _relatedArea = GuideParts.Card(GuideParts.Vertical(relatedTitle, _relatedRows));
        NameControl(_relatedArea, "You may also want to read");
        var shareTitle = GuideParts.Body("What would you like to do with this answer?"); shareTitle.FontWeight = FontWeight.SemiBold;
        _shareCopy.Content = ChoiceContent(GuideParts.Body("Copy for email"), "A clean summary to paste", "M3,6 H21 V18 H3 Z M3,6 L12,13 L21,6");
        _sharePdf.Content = ChoiceContent(GuideParts.Body("Save as PDF"), "A full A4 report for decisions", "M6,3 H14 L19,8 V21 H6 Z M14,3 V8 H19 M9,13 H16 M9,17 H16");
        _shareNote.Content = ChoiceContent(GuideParts.Body("Add to my note"), "An editable report in Tern", "M5,4 H19 V20 H5 Z M8,9 H16 M8,13 H16 M8,17 H13");
        _sharePrint.Content = ChoiceContent(_sharePrintTitle, "A full A4 report on paper", "M7,8 V3 H17 V8 M5,8 H19 V17 H17 M7,17 H5 V8 M7,14 H17 V21 H7 Z");
        var options = new Avalonia.Controls.Primitives.UniformGrid { Columns = 2, Children = { _shareCopy, _sharePdf, _shareNote, _sharePrint } };
        var page = GuideParts.Vertical(answerCard, _relatedArea, GuideParts.Card(GuideParts.Vertical(shareTitle, options, _sharePrintHelp, _shareBar)));
        NameControl(page, "Your answer");
        return page;
    }

    private Control ChoiceContent(TextBlock title, string description, string icon)
    {
        title.FontWeight = FontWeight.SemiBold;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(icon), Width = 22, Height = 22, Stretch = Stretch.Uniform, StrokeThickness = 1.6, VerticalAlignment = VerticalAlignment.Center,
            [!Avalonia.Controls.Shapes.Shape.StrokeProperty] = this.GetResourceObservable(Tern.Desktop.Themes.ThemeManager.Accent).ToBinding(),
        };
        var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        row.Children.Add(path);
        var text = new StackPanel { Spacing = 2, Children = { title, GuideParts.Caption(description) } };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private StackPanel BuildReportPage()
    {
        NameControl(_reportTitle, "Report title"); NameControl(_reportBy, "Report prepared by"); NameControl(_reportHeadline, "Report headline");
        NameControl(_reportRecommendation, "Report recommendation"); NameControl(_reportFindings, "Report key findings"); NameControl(_reportReviewed, "Report reviewed");
        NameControl(_reportRun, "Finish report"); NameControl(_reportBar.Label, "Report help"); NameControl(_reportNumbers.Label, "Report numbers to check"); NameControl(_reportPrinter, "Report printer"); NameControl(_reportPartial.Label, "Report partly read"); NameControl(_reportCheck.Label, "AI report check");
        NameControl(_reportConditions, "Report conditions"); NameControl(_reportRisks, "Report summary risks"); NameControl(_reportNextAction, "Report next action");
        NameControl(_reportFinalAssessment, "Report final assessment"); NameControl(_reportPreview, "Preview report");
        _reportPreview.Click += async (_, _) => await PreviewReportAsync();
        NameControl(_reportCorrect, "Ask AI to correct these issues");
        _reportCorrect.Click += async (_, _) => await RunAsync(CorrectReportAsync);
        ToolTip.SetShowOnDisabled(_reportCorrect, true);
        _reportReviewed.IsCheckedChanged += (_, _) => RefreshReportAvailability();
        foreach (var box in new[] { _reportTitle, _reportBy, _reportHeadline, _reportRecommendation, _reportFindings, _reportConditions, _reportRisks, _reportNextAction, _reportFinalAssessment })
            box.PropertyChanged += (_, e) =>
            {
                if (e.Property != TextBox.TextProperty || _loadingReport || !_reportLoaded) return;
                _reportEditRevision++;
                CaptureReportEdits(); _reportReviewed.IsChecked = false; InvalidateCheckedContent();
                if (_reportIssues.Length > 0) ShowReportIssues(); else _reportCheck.Clear();
                RefreshReportPreview(); RefreshReportAvailability(); QueueCheckpoint();
            };
        _reportRun.Click += async (_, _) => await RunAsync(FinishReportAsync);
        var missingTitle = GuideParts.Body("Not found in your notes"); missingTitle.FontWeight = FontWeight.SemiBold;
        _reportMissingArea = new Expander { Header = "Missing information", IsExpanded = false, Content = GuideParts.Vertical(GuideParts.Caption("The report shows these as missing, with who to ask."), _reportMissing) };
        var edits = new Expander
        {
            Header = "Edit more report details", IsExpanded = false,
            Content = GuideParts.Vertical(_reportFindingsLabel, _reportFindings,
                GuideParts.Caption("Key conditions (one per line)"), _reportConditions, GuideParts.Caption("Important risks (one per line)"), _reportRisks,
                GuideParts.Caption("Next action"), _reportNextAction, GuideParts.Caption("Final assessment"), _reportFinalAssessment),
        };
        var page = GuideParts.Vertical(GuideParts.Title("Is this report right?"), GuideParts.Body("Your A4 report is ready. Check the main points."),
            GuideParts.Caption("Report title"), _reportTitle, GuideParts.Caption("Prepared by"), _reportBy,
            _reportHeadlineLabel, _reportHeadline, _reportRecommendationLabel, _reportRecommendation, edits,
            _reportIncludes, _reportMissingArea, _reportPartial, _reportNumbers,
            _reportPreview, _reportCheck, _reportIssueDetails, _reportCorrect, _reportPrinter, _reportReviewed, BuildReportOutputs());
        NameControl(page, "Check your report");
        return page;
    }

    private void ClearAnswer()
    {
        _answerLines.Children.Clear(); _foundIn.Children.Clear(); _relatedRows.Children.Clear();
        _answerSources = []; _draft = null; _draftTurn = null; _shareResult = null;
        _reportMaterial = ""; _reportCoverage = ""; _reportCheckedKey = null; _reportCheck.Clear();
        _reportIssues = []; _reportIssuesKey = null;
        _reportLoaded = false;
        _cachedNotes = []; _cachedCoverage = null; _sourceStates.Clear(); _contentSourceKey = ""; InvalidateCheckedContent();
        foreach (var box in new[] { _reportTitle, _reportBy, _reportHeadline, _reportRecommendation, _reportFindings, _reportConditions, _reportRisks, _reportNextAction, _reportFinalAssessment }) box.Text = "";
        _reportMissing.Children.Clear(); _reportIssueDetails.Content = null; _reportIssueDetails.IsVisible = false;
        _shareCopy.IsChecked = true;
    }

    private void RenderAnswer(WorkspaceSession root, KnowledgeAnswer answer, bool preserveReport = false)
    {
        _answerLines.Children.Clear(); _foundIn.Children.Clear(); _relatedRows.Children.Clear();
        if (!preserveReport) { _draft = null; _draftTurn = null; _shareResult = null; _shareCopy.IsChecked = true; }
        foreach (var line in KnowledgeAnswerExport.Lines(answer))
            _answerLines.Children.Add(new SelectableTextBlock { Text = "• " + line, TextWrapping = TextWrapping.Wrap, [!TextBlock.FontSizeProperty] = GuideParts.Font("Lead").ToBinding() });
        foreach (var citation in answer.Citations)
        {
            var source = citation.Source;
            var open = new Button { Content = new TextBlock { Text = Path.GetFileNameWithoutExtension(source.RelativePath) } };
            NameControl(open, $"Open note from answer: {source.RelativePath}");
            ToolTip.SetTip(open, "Open this note to check the answer.");
            open.Click += async (_, _) => await RunAsync(async token => { await _retrieval.ValidateAsync(root, [source], Limits, token); await _open(source.Path, source.Line); });
            _foundIn.Children.Add(open);
        }
        var used = answer.Citations.Select(citation => citation.Source.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chosen = _answerSources.Select(source => source.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var note in _foundRelated.Where(note => !used.Contains(note.Path) && !chosen.Contains(note.Path)).Take(3))
        {
            var name = GuideParts.Body(Path.GetFileNameWithoutExtension(note.RelativePath)); name.FontWeight = FontWeight.SemiBold;
            var reason = GuideParts.Caption(note.Relationship switch
            {
                "Linked from a retrieved note" => "Linked from a note in this answer",
                "Links to a retrieved note" => "Mentions a note in this answer",
                "Shares a tag with a retrieved note" => "Has the same tag as a note in this answer",
                AiSuggestedNote => AiSuggestedNote,
                _ => "Related to this answer"
            });
            var open = new Button { Content = "Open", VerticalAlignment = VerticalAlignment.Center };
            NameControl(open, $"Open related note: {note.RelativePath}");
            open.Click += async (_, _) => await RunAsync(async token => { await _retrieval.ValidateRelatedAsync(root, note, Limits, token); await _open(note.Path, 1); });
            var row = new Grid { ColumnDefinitions = new("*,Auto") };
            row.Children.Add(new StackPanel { Spacing = 1, Children = { name, reason } });
            Grid.SetColumn(open, 1); row.Children.Add(open);
            _relatedRows.Children.Add(row);
        }
        _relatedArea.IsVisible = _relatedRows.Children.Count > 0;
    }

    private void RefreshShare()
    {
        // Asking Windows can stall on a slow print service, and this runs on every keystroke. Ask once per visit to the answer page.
        if (_stage is AnswerStage or ReportStage && !_printerChecked) { _printerName = DefaultPrinterName(); _printerChecked = true; }
        var printer = _printerName;
        _sharePrintTitle.Text = printer is null ? "Print" : "Print on " + printer;
        ToolTip.SetTip(_sharePrint, _sharePrintTitle.Text);
        _sharePrint.IsEnabled = printer is not null && _work is null;
        ShowMessage(_sharePrintHelp, printer is null, MessageTone.Warning, "No printer found. Add one in Windows Settings.");
        if (printer is null && _sharePrint.IsChecked == true) _shareCopy.IsChecked = true;
        foreach (var option in new[] { _shareCopy, _sharePdf, _shareNote }) option.IsEnabled = _work is null;
        _shareRun.Content = "Use this checked report";
        var aiProblem = _evidenceNeedsRefresh ? "Your notes changed. Press Refresh notes before sharing."
            : ContentReady ? null
            : Model is null ? "Add an AI connection to create a report."
            : _connectionProblem is { } connection ? connection
            : _shell.Settings.OfflineMode ? "Offline mode is on. Turn it off in Settings to create a report."
            : !CanTransmit(Model) ? "Tern needs your OK to use online AI. Allow it in Settings."
            : null;
        _shareRun.IsEnabled = _work is null && _conversation.Turns.Count > 0 && aiProblem is null;
        if (_work is not null) _shareBar.Clear();
        else if (aiProblem is not null) _shareBar.Show(_connectionProblem is not null ? MessageTone.Error : MessageTone.Warning, aiProblem);
        else if (_shareResult is { } result) _shareBar.Show(result.Tone, result.Text);
        else _shareBar.Show(MessageTone.Info, "Review one complete report before sharing it. Every format uses your latest reviewed report. Copying or exporting keeps this task open until you choose Done.");
        SynchronizeReportOutputs();
    }

    private void RefreshReportAvailability()
    {
        var complete = !string.IsNullOrWhiteSpace(_reportTitle.Text) && !string.IsNullOrWhiteSpace(_reportHeadline.Text) && !string.IsNullOrWhiteSpace(_reportRecommendation.Text);
        var reviewed = _reportReviewed.IsChecked == true;
        _reportRun.Content = !ContentReady ? "Check report" : _sharePdf.IsChecked == true ? "Save PDF" : _shareNote.IsChecked == true ? "Review insertion" : _shareCopy.IsChecked == true ? "Copy for email" : "Print now";
        var connectionReady = Model is { } model && CanTransmit(model) && _connectionProblem is null;
        _reportPreview.IsEnabled = _draft is not null && _work is null;
        foreach (var box in new[] { _reportTitle, _reportBy, _reportHeadline, _reportRecommendation, _reportFindings, _reportConditions, _reportRisks, _reportNextAction, _reportFinalAssessment }) box.IsEnabled = _work is null;
        _reportReviewed.IsEnabled = _work is null;
        _reportRun.IsEnabled = _work is null && _draft is not null && complete && (ContentReady ? reviewed : connectionReady) && !_evidenceNeedsRefresh;
        _reportCorrect.IsVisible = _stage == ReportStage && _reportIssues.Length > 0;
        _reportCorrect.IsEnabled = _work is null && IssuesMatchReport() && connectionReady && !_evidenceNeedsRefresh;
        ToolTip.SetTip(_reportCorrect, _evidenceNeedsRefresh ? "Press Refresh notes first; your current report and issues are kept."
            : !connectionReady ? "Choose an available AI connection and turn off Offline mode to correct the report."
            : !IssuesMatchReport() ? "The report changed since this check. Review it and check the report again to get current issues."
            : "Send this current report, these exact issues and your ticked notes to AI once. A checked correction will need your review before sharing.");
        ToolTip.SetTip(_reportRun, _evidenceNeedsRefresh ? "Press Refresh notes first." : !ContentReady && !connectionReady ? "Choose an available AI connection to check the report." : !ContentReady ? "Check these edits before sharing. This does not export anything." : !reviewed ? "Read the report and tick I reviewed this report." : "Use the already checked report. No AI request is needed.");
        ToolTip.SetShowOnDisabled(_reportRun, true);
        _reportPrinter.IsVisible = _sharePrint.IsChecked == true && _printerName is not null;
        _reportPrinter.Text = "Your printer: " + _printerName;
        foreach (var box in new[] { _reportTitle, _reportHeadline, _reportRecommendation })
            box.Classes.Set("rename-error", _draft is not null && string.IsNullOrWhiteSpace(box.Text));
        var (tone, text) = !complete ? (MessageTone.Warning, "Fill in the title and main points to continue.")
            : !ContentReady ? (MessageTone.Info, "Check your edits before sharing the report.")
            : !reviewed ? (MessageTone.Warning, "Tick \u201cI reviewed this report\u201d to continue.")
            : (MessageTone.Success, "Report checked. Choose what to do.");
        ShowMessage(_reportBar, _work is null && _stage == ReportStage && _draft is not null, tone, text);
    }

    private async Task CopyAsync(string text)
    {
        if (CopyText is { } copy) { await copy(text); return; }
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("The clipboard is not available right now.");
        await clipboard.SetTextAsync(text);
    }

    private async Task ShareAsync(CancellationToken token)
    {
        if (_conversation.Turns.Count == 0) return;
        var turn = _conversation.Turns[^1];
        await ValidateTaskEvidenceAsync(token);
        if (_draft is null || !ReferenceEquals(_draftTurn, turn))
        {
            _status.Text = "Preparing your A4 report. This can take a little longer…";
            _draft = await BuildReportAsync(turn, token);
            _draftTurn = turn;
            if (_retainedDraft is { } retained)
            {
                var wording = retained.Report;
                _draft = _draft with { Report = _draft.Report with
                {
                    Title = wording.Title, PreparedBy = wording.PreparedBy, Headline = wording.Headline,
                    Recommendation = wording.Recommendation, Reasons = wording.Reasons, SummaryConditions = wording.SummaryConditions,
                    SummaryRisks = wording.SummaryRisks, SummaryNextAction = wording.SummaryNextAction, FinalAssessment = wording.FinalAssessment,
                } };
                _retainedDraft = null;
            }
            LoadReport(_draft);
            await PrepareCurrentContentAsync(token);
            _status.Text = "";
        }
        else CaptureReportEdits();
        ShowStage(ReportStage);
    }

    private async Task<KnowledgeReportDraft> BuildReportAsync(KnowledgeTurn turn, CancellationToken token)
    {
        if (Scope is not { } root || Model is not { } model || !CanTransmit(model)) throw new InvalidOperationException("The AI connection is not available. Check it, then try again.");
        _sending = true;
        var taskId = _taskId; var generation = _generation;
        bool Current() => ScopeCurrent(root) && _taskId == taskId && _generation == generation && Model == model && CanTransmit(model)
            && ReferenceEquals(_conversation.Turns.LastOrDefault(), turn) && !token.IsCancellationRequested;
        if (_cachedNotes.Length == 0) await CacheCurrentNotesAsync(root, _answerSources, token);
        var notes = CachedNotes;
        var reader = NewReader(model, Current);
        int Room() => KnowledgeReportBuilder.MaterialRoom(turn.Question, reader.Limit);
        var read = await reader.ReadAllAsync(turn.Question, notes, Room, token);
        _status.Text = "Writing your report…";
        _reportCoverage = reader.Coverage(_cachedCoverage is { } coverage ? read with { CharactersRead = coverage.Read, CharactersTotal = coverage.Total, Notes = coverage.Notes, NotRead = coverage.NotRead } : read);
        _reportMaterial = read.Material;
        async Task<string> Ask(AIRequest request, CancellationToken stepToken)
        {
            await ValidateTaskEvidenceAsync(stepToken);
            if (!Current()) throw new OperationCanceledException(stepToken);
            _status.Text = request.Purpose == AIPurpose.ReportRepair ? "AI is correcting the source quotations…" : "Preparing your report…";
            var response = await _ai.SendWritingAsync(model.Id, _ai.PrepareNoteText(model.Id, request), Current, stepToken, skipCopilotCheck: true);
            await ValidateTaskEvidenceAsync(stepToken);
            if (!Current()) throw new OperationCanceledException(stepToken);
            return AIWorkspaceService.RestoreNoteText(response);
        }
        KnowledgeReportDraft draft;
        try
        {
            var (reply, material) = await reader.WriteRequestAsync(turn.Question, text => KnowledgeReportBuilder.ReportRequest(turn.Question, text), read.Material, Room, token);
            _reportMaterial = material;
            draft = await KnowledgeReportSections.ReadAsync(reply, turn.Question, material, notes, _reportBy.Text ?? "", DateTimeOffset.Now,
                Math.Min(reader.Limit, _ai.SendCharactersFor(model.Id)), Ask, token);
        }
        catch (AIReplyIncompleteException)
        {
            _status.Text = "Finishing the remaining report sections…";
            draft = await KnowledgeReportSections.CompleteAsync(turn.Question, read.Material, notes, _reportBy.Text ?? "", DateTimeOffset.Now,
                Math.Min(reader.Limit, _ai.SendCharactersFor(model.Id)), Ask, token);
        }
        await ValidateTaskEvidenceAsync(token);
        if (!Current()) throw new OperationCanceledException(token);
        return draft with { Report = draft.Report with { Coverage = _reportCoverage } };
    }

    private void LoadReport(KnowledgeReportDraft draft)
    {
        _loadingReport = true;
        var report = draft.Report;
        _reportTitle.Text = report.Title;
        _reportHeadline.Text = report.Headline;
        _reportRecommendation.Text = report.Recommendation;
        _reportHeadlineLabel.Text = report.HasDecision ? "Decision needed" : "Main question";
        _reportRecommendationLabel.Text = report.HasDecision ? "Recommendation" : "Key finding";
        _reportFindingsLabel.Text = report.HasDecision ? "Why this is the best choice (one per line)" : "More key findings (one per line)";
        _reportFindings.Text = string.Join('\n', report.Reasons);
        _reportBy.Text = report.PreparedBy;
        _reportConditions.Text = string.Join('\n', report.SummaryConditions); _reportRisks.Text = string.Join('\n', report.SummaryRisks);
        _reportNextAction.Text = report.SummaryNextAction; _reportFinalAssessment.Text = report.FinalAssessment;
        var parts = new List<string>();
        void Count(int count, string one, string many) { if (count > 0) parts.Add(count == 1 ? "1 " + one : $"{count} {many}"); }
        Count(report.Options.Count, "option", "options"); Count(report.Risks.Count, "risk", "risks"); Count(report.Costs.Count, "cost", "costs"); Count(report.Actions.Count, "action", "actions"); Count(report.Evidence.Count, "fact from your notes", "facts from your notes");
        _reportIncludes.Text = ((parts.Count > 0 ? "The report includes " + string.Join(", ", parts) + ". " : "") + _reportCoverage).Trim();
        _reportMissing.Children.Clear();
        foreach (var item in report.Missing) _reportMissing.Children.Add(GuideParts.Caption($"• {item.Item}: ask {item.WhoCanConfirm}"));
        _reportMissingArea.IsVisible = report.Missing.Count > 0;
        ShowMessage(_reportPartial, report.Missing.Any(item => item.Item == "Some long notes were only partly read"), MessageTone.Warning,
            "Some long notes were only partly read, so this report may leave out facts. Check those notes before you share it.");
        ShowMessage(_reportNumbers, draft.NumbersToCheck.Count > 0, MessageTone.Warning, "Some figures need review. Check their explanations in the report preview before sharing.");
        _reportReviewed.IsChecked = false;
        _reportCheckedKey = null; _reportCheck.Clear();
        _reportLoaded = true; _loadingReport = false; RefreshReportPreview();
    }

    private DecisionReport EditedReport() => _draft!.Report with
    {
        Title = _reportTitle.Text!.Trim(), PreparedBy = _reportBy.Text?.Trim() ?? "",
        Headline = _reportHeadline.Text!.Trim(), Recommendation = _reportRecommendation.Text!.Trim(),
        Reasons = (_reportFindings.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        SummaryConditions = (_reportConditions.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        SummaryRisks = (_reportRisks.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        SummaryNextAction = _reportNextAction.Text?.Trim() ?? "", FinalAssessment = _reportFinalAssessment.Text?.Trim() ?? "",
    };

    private void CaptureReportEdits()
    {
        if (_draft is not null && _reportLoaded && !_loadingReport) _draft = _draft with { Report = EditedReport() };
    }

    private void RefreshReportPreview()
    {
        _reportPreview.IsEnabled = _draft is not null && _work is null;
    }

    private async Task FinishReportAsync(CancellationToken token)
    {
        if (_draft is null || Scope is not { } root || _conversation.Turns.LastOrDefault() is not { } turn) return;
        if (!ContentReady) { await PrepareCurrentContentAsync(token); return; }
        if (_reportReviewed.IsChecked != true) return;
        var content = _checkedContent!;
        var pdf = _cachedReportPdf!;
        var report = content.Report;
        var task = _taskId; var generation = _generation; var revision = _reportEditRevision;
        bool Current() => !_disposed && task == _taskId && generation == _generation && revision == _reportEditRevision
            && ScopeCurrent(root) && ReferenceEquals(content, _checkedContent) && ContentReady && !token.IsCancellationRequested;

        // The destination picker is the first awaited work after a ready Save PDF click.
        string? destination = null;
        if (_sharePdf.IsChecked == true)
        {
            destination = await ChoosePdfPathAsync(report.Title);
            if (destination is null) { _status.Text = "Saving was cancelled. Your checked report is still here."; return; }
        }
        await ValidateTaskEvidenceAsync(token);
        if (!Current()) throw new InvalidOperationException("This task changed. Your report is kept; check it before sharing.");
        string done;
        if (_shareCopy.IsChecked == true)
        {
            await CopyAsync(content.Email);
            done = "Copied. Paste the management email into your email app.";
        }
        else if (_sharePdf.IsChecked == true)
        {
            DecisionReportPdf.SaveRendered(pdf, destination!, token);
            done = "Saved as " + Path.GetFileName(destination) + ".";
        }
        else if (_sharePrint.IsChecked == true && _printerName is { } printer)
        {
            await PrintPdf(report.Title, pdf.Data.ToArray(), token);
            done = "Sent to " + printer + ".";
        }
        else if (_shareNote.IsChecked == true)
        {
            _insertionPending = true;
            var sourceRevision = _sourceRevision;
            bool InsertionCurrent() => Current() && _insertionPending && sourceRevision == _sourceRevision;
            _review(turn.Answer, content.Markdown, InsertionCurrent, async check =>
            {
                if (!InsertionCurrent()) throw new InvalidOperationException("This task changed. Review the report again before inserting it.");
                await ValidateTaskEvidenceAsync(check);
                if (!InsertionCurrent()) throw new InvalidOperationException("This task changed. Review the report again before inserting it.");
            });
            done = "Review the report in your note before inserting it.";
        }
        else throw new InvalidOperationException("No printer found. Add one in Windows Settings.");
        token.ThrowIfCancellationRequested();
        _shareResult = (_shareNote.IsChecked == true ? MessageTone.Info : MessageTone.Success, done);
        _status.Text = done;
        ShowStage(ReportStage);
    }

    private async Task<string?> ChoosePdfPathAsync(string title)
    {
        var safe = string.Concat(title.Where(character => !Path.GetInvalidFileNameChars().Contains(character))).Trim();
        var suggested = (safe.Length > 0 ? safe : "Report") + ".pdf";
        if (PickPdfPath is { } pick) return await pick(suggested);
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage) throw new InvalidOperationException("Saving is not available in this window.");
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save report as PDF", SuggestedFileName = suggested, DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF document") { Patterns = ["*.pdf"] }]
        });
        return file?.TryGetLocalPath();
    }
}
