namespace Tern.App.Commands;

/// <summary>Stable command identifiers; persisted in key-binding overrides, so never rename one.</summary>
public static class CommandIds
{
    public const string LibraryFiles = "nav.libraryFiles";
    public const string CreateWorkspace = "workspace.create";
    public const string CloseAllWorkspaces = "workspace.closeAll";
    public const string LibraryRecent = "nav.libraryRecent";
    public const string LibrarySearch = "nav.librarySearch";
    public const string LibraryBacklinks = "nav.libraryBacklinks";
    public const string LibraryBrokenLinks = "nav.libraryBrokenLinks";
    public const string LibraryTasks = "nav.libraryTasks";
    public const string LibraryTags = "nav.libraryTags";
    public const string LibraryMentions = "nav.libraryMentions";
    public const string LibraryGraph = "nav.libraryGraph";
    public const string FoldSection = "nav.foldSection";
    public const string MoveSectionUp = "format.moveSectionUp";
    public const string MoveSectionDown = "format.moveSectionDown";
    public const string PromoteSection = "format.promoteSection";
    public const string DemoteSection = "format.demoteSection";
    public const string WorkspacePrivacy = "workspace.privacy";
    public const string ClearFileHistory = "file.clearHistory";
    public const string ClearWorkspaceHistory = "workspace.clearHistory";
    public const string RecentlyDiscarded = "history.discarded";
    public const string GitDiff = "file.gitDiff";
    public const string Help = "app.help";
    public const string AIConnections = "app.aiConnections";
    public const string AskAI = "edit.askAI";
    public const string AskKnowledge = "edit.askKnowledge";
    public const string Reset = "app.reset";
    public const string ClearRecent = "history.clear";
    public const string Rename = "file.rename";
    public const string MoveDocument = "file.move";
    public const string DuplicateDocument = "file.duplicate";
    public const string NewFile = "file.new";
    public const string NewFromTemplate = "file.newFromTemplate";
    public const string OpenTodaysNote = "file.openTodaysNote";
    public const string OpenFile = "file.open";
    public const string OpenUrl = "file.openUrl";
    public const string NewWorkspaceFile = "workspace.newFile";
    public const string NewFolder = "workspace.newFolder";
    public const string Print = "file.print";
    public const string Undo = "edit.undo";
    public const string Redo = "edit.redo";
    public const string Cut = "edit.cut";
    public const string Copy = "edit.copy";
    public const string Paste = "edit.paste";
    public const string PastePlain = "edit.pastePlain";
    public const string PasteTable = "edit.pasteTable";
    public const string SelectAll = "edit.selectAll";
    public const string OpenFolder = "workspace.openFolder";
    public const string OpenWorkspace = "workspace.open";
    public const string PromoteFolder = "workspace.promoteFolder";
    public const string Save = "file.save";
    public const string SaveAs = "file.saveAs";
    public const string SaveAll = "file.saveAll";
    public const string CloseWorkspace = "workspace.close";
    public const string CloseDocuments = "tab.closeAll";
    public const string CloseTab = "tab.close";
    public const string NextTab = "tab.next";
    public const string PreviousTab = "tab.previous";
    public const string ReopenClosedTab = "tab.reopenClosed";
    public const string QuickOpen = "nav.quickOpen";
    public const string CommandPalette = "nav.commandPalette";
    public const string GoToLine = "nav.goToLine";
    public const string GoToHeading = "nav.goToHeading";
    public const string Find = "edit.find";
    public const string Replace = "edit.replace";
    public const string FindInWorkspace = "search.inWorkspace";
    public const string ReplaceInWorkspace = "search.replaceWorkspace";
    public const string ViewSource = "view.source";
    public const string ViewSplit = "view.split";
    public const string ViewReading = "view.reading";
    public const string ToggleSidebar = "view.toggleSidebar";
    public const string ToggleRightPanel = "view.toggleRightPanel";
    public const string ToggleTheme = "view.toggleTheme";
    public const string ShowHistory = "file.history";
    public const string ToggleReference = "view.toggleReference";
    public const string ResetLayout = "view.resetLayout";
    public const string Welcome = "help.welcome";
    public const string ToggleFocus = "view.toggleFocus";
    public const string ZoomIn = "view.zoomIn";
    public const string ZoomOut = "view.zoomOut";
    public const string ZoomReset = "view.zoomReset";
    public const string ReadCurrentLine = "view.readCurrentLine";
    public const string ReadDocumentStatus = "view.readDocumentStatus";
    public const string Bold = "format.bold";
    public const string Italic = "format.italic";
    public const string InlineCode = "format.code";
    public const string Strikethrough = "format.strikethrough";
    public const string InsertLink = "format.link";
    public const string HeadingUp = "format.headingUp";
    public const string HeadingDown = "format.headingDown";
    public const string ToggleQuote = "format.quote";
    public const string ToggleBullet = "format.bullet";
    public const string ToggleNumbered = "format.numbered";
    public const string ToggleTask = "format.task";
    public const string InsertTable = "insert.table";
    public const string Indent = "format.indent";
    public const string Outdent = "format.outdent";
    public const string TableInsertRowAbove = "format.table.rowAbove";
    public const string TableInsertRowBelow = "format.table.rowBelow";
    public const string TableInsertColumnLeft = "format.table.columnLeft";
    public const string TableInsertColumnRight = "format.table.columnRight";
    public const string TableDeleteRow = "format.table.deleteRow";
    public const string TableDeleteColumn = "format.table.deleteColumn";
    public const string TableMoveColumnLeft = "format.table.moveLeft";
    public const string TableMoveColumnRight = "format.table.moveRight";
    public const string TableAlignLeft = "format.table.alignLeft";
    public const string TableAlignCenter = "format.table.alignCenter";
    public const string TableAlignRight = "format.table.alignRight";
    public const string TableAlignNone = "format.table.alignNone";
    public const string TableSortAscending = "format.table.sortAscending";
    public const string TableSortDescending = "format.table.sortDescending";
    public const string TableFormat = "format.table.format";
    public const string InsertImage = "insert.image";
    public const string Heading1 = "format.heading1";
    public const string Heading2 = "format.heading2";
    public const string Heading3 = "format.heading3";
    public const string InsertCodeBlock = "insert.codeBlock";
    public const string InsertRule = "insert.rule";
    public const string PasteImage = "insert.pasteImage";
    public const string ToggleTaskAtCaret = "task.toggleAtCaret";
    public const string ExportHtml = "export.html";
    public const string ExportAs = "export.as";
    public const string ExportPdf = "export.pdf";
    public const string Settings = "app.settings";
    public const string Shortcuts = "app.shortcuts";
    public const string About = "app.about";
    public const string ReloadFromDisk = "file.reload";
    public const string RevealInExplorer = "file.reveal";
    public const string ReviewSaveFragments = "file.saveFragments";
    public const string ReopenWithEncoding = "file.reopenWithEncoding";
    public const string ChangeEncoding = "file.changeEncoding";
    public const string ChangeLineEnding = "file.changeLineEnding";
    public const string Exit = "app.exit";
}

public sealed record CommandDefinition(string Id, string Title, string Category, string? DefaultGesture = null);

/// <summary>A normalised keyboard gesture such as Ctrl+Shift+P. Normalising makes "shift+ctrl+p" and "Ctrl+Shift+P" equal.</summary>
public readonly record struct Gesture(bool Ctrl, bool Alt, bool Shift, bool Meta, string Key)
{
    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = "Escape",
        ["return"] = "Enter",
        ["del"] = "Delete",
        ["plus"] = "OemPlus",
        ["+"] = "OemPlus",
        ["="] = "OemPlus",
        ["minus"] = "OemMinus",
        ["-"] = "OemMinus",
        ["comma"] = "OemComma",
        [","] = "OemComma",
        ["pgup"] = "PageUp",
        ["pgdn"] = "PageDown",
    };

    public static bool TryParse(string? text, out Gesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        bool ctrl = false, alt = false, shift = false, meta = false;
        string? key = null;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // "Ctrl++" means the plus key; a lone trailing separator ("Ctrl+") is an unfinished gesture.
        var trimmed = text.TrimEnd();
        var trailingPlus = trimmed == "+" || trimmed.EndsWith("++", StringComparison.Ordinal);
        if (trailingPlus && parts.Length > 0)
        {
            parts = [.. parts, "+"];
        }

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    ctrl = true;
                    break;
                case "alt":
                    alt = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "meta" or "cmd" or "win" or "super":
                    meta = true;
                    break;
                default:
                    if (key is not null)
                    {
                        return false;
                    }

                    key = KeyAliases.TryGetValue(part, out var alias) ? alias : Normalize(part);
                    break;
            }
        }

        if (key is null || !IsValidKey(key))
        {
            return false;
        }

        gesture = new Gesture(ctrl, alt, shift, meta, key);
        return true;
    }

    /// <summary>Gesture text for people: "Ctrl+/" rather than the stored "Ctrl+OemQuestion".</summary>
    public static string Describe(string? text)
    {
        if (!TryParse(text, out var gesture))
        {
            return text ?? string.Empty;
        }

        var key = gesture.Key switch
        {
            "OemQuestion" => "/",
            "OemComma" => ",",
            "OemPeriod" => ".",
            "OemPlus" => "=",
            "OemMinus" => "-",
            "OemOpenBrackets" => "[",
            "OemCloseBrackets" => "]",
            "OemSemicolon" => ";",
            "OemQuotes" => "'",
            "OemTilde" => "`",
            "OemPipe" or "OemBackslash" => "\\",
            var k when k.Length == 2 && k[0] == 'D' && char.IsAsciiDigit(k[1]) => k[1..],
            var k => k,
        };
        return new Gesture(gesture.Ctrl, gesture.Alt, gesture.Shift, gesture.Meta, key).ToString();
    }

    /// <summary>True for keys that type a character on some layout (letters, digits, punctuation, Space).</summary>
    public bool IsPrintableKey => Key.Length == 1 || Key == "Space" || Key.StartsWith("Oem", StringComparison.Ordinal)
        || Key.Length == 2 && Key[0] == 'D' && char.IsAsciiDigit(Key[1]);

    /// <summary>Windows reports AltGr as Ctrl+Alt, so Ctrl+Alt(+Shift)+printable can be a typed character instead of a shortcut.</summary>
    public bool CollidesWithAltGr => Ctrl && Alt && !Meta && IsPrintableKey;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl)
        {
            parts.Add("Ctrl");
        }

        if (Alt)
        {
            parts.Add("Alt");
        }

        if (Shift)
        {
            parts.Add("Shift");
        }

        if (Meta)
        {
            parts.Add("Meta");
        }

        parts.Add(Key);
        return string.Join('+', parts);
    }

    private static readonly System.Text.RegularExpressions.Regex ValidKey = new(
        @"^(Oem[A-Za-z0-9]+|[A-Z]|D[0-9]|F([1-9]|1[0-9]|2[0-4])|NumPad[0-9]|Enter|Escape|Tab|Space|Back|Delete|Insert|Home|End|PageUp|PageDown|Up|Down|Left|Right)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string Normalize(string key)
    {
        if (key.Length == 1)
        {
            return char.IsDigit(key[0]) ? "D" + key : key.ToUpperInvariant();
        }

        return char.ToUpperInvariant(key[0]) + key[1..];
    }

    public static bool IsValidKey(string key) => ValidKey.IsMatch(key);
}

public enum BindingProblem
{
    None,
    Invalid,
    Conflict,
    UnknownCommand,
}

public sealed record BindingResult(BindingProblem Problem, string? ConflictingCommandId = null)
{
    public bool Succeeded => Problem == BindingProblem.None;
}

/// <summary>All commands plus the effective key bindings (defaults overlaid with the user's overrides).</summary>
public sealed class CommandRegistry
{
    // New defaults may not displace a shortcut a user assigned in an earlier build.
    private static readonly IReadOnlyDictionary<string, string> AdditionalDefaultGestures = new Dictionary<string, string>
    {
        [CommandIds.LibraryFiles] = "Ctrl+Shift+F1",
        [CommandIds.LibraryRecent] = "Ctrl+Shift+F2",
        [CommandIds.LibrarySearch] = "Ctrl+Shift+F3",
        [CommandIds.LibraryBacklinks] = "Ctrl+Shift+F4",
        [CommandIds.LibraryBrokenLinks] = "Ctrl+Shift+F5",
        [CommandIds.LibraryTasks] = "Ctrl+Shift+F6",
        [CommandIds.ReplaceInWorkspace] = "Ctrl+Alt+Shift+F13",
        [CommandIds.LibraryTags] = "Ctrl+Shift+F14",
        [CommandIds.LibraryMentions] = "Ctrl+Shift+F15",
        [CommandIds.LibraryGraph] = "Ctrl+Shift+F16",
        [CommandIds.FoldSection] = "Ctrl+Alt+Shift+F14",
        [CommandIds.MoveSectionUp] = "Ctrl+Alt+Shift+F15",
        [CommandIds.MoveSectionDown] = "Ctrl+Alt+Shift+F16",
        [CommandIds.PromoteSection] = "Ctrl+Alt+Shift+F17",
        [CommandIds.DemoteSection] = "Ctrl+Alt+Shift+F18",
        [CommandIds.WorkspacePrivacy] = "Ctrl+Alt+Shift+F19",
        [CommandIds.ClearFileHistory] = "Ctrl+Alt+Shift+F20",
        [CommandIds.ClearWorkspaceHistory] = "Ctrl+Alt+Shift+F21",
        [CommandIds.RecentlyDiscarded] = "Ctrl+Alt+Shift+F22",
        [CommandIds.GitDiff] = "Ctrl+Alt+Shift+F23",
        [CommandIds.CreateWorkspace] = "Ctrl+Shift+F7",
        [CommandIds.OpenWorkspace] = "Ctrl+Alt+Shift+F11",
        [CommandIds.PromoteFolder] = "Ctrl+Alt+Shift+F7",
        [CommandIds.MoveDocument] = "Ctrl+Alt+Shift+F5",
        [CommandIds.DuplicateDocument] = "Ctrl+Alt+Shift+F6",
        [CommandIds.CloseAllWorkspaces] = "Ctrl+Shift+F9",
        [CommandIds.AIConnections] = "Ctrl+Shift+A",
        [CommandIds.AskAI] = "Ctrl+Shift+Space",
        [CommandIds.AskKnowledge] = "Ctrl+Alt+F2",
        // Irreversible commands use a four-key chord on non-printable keys so they cannot be typed by accident.
        [CommandIds.Reset] = "Ctrl+Alt+Shift+F9",
        [CommandIds.ClearRecent] = "Ctrl+Alt+Shift+F8",
        [CommandIds.ReviewSaveFragments] = "Ctrl+Alt+Shift+F4",
        [CommandIds.OpenUrl] = "Ctrl+Shift+U",
        [CommandIds.NewWorkspaceFile] = "Ctrl+Shift+N",
        [CommandIds.NewFolder] = "Ctrl+Shift+F8",
        [CommandIds.ReloadFromDisk] = "F5",
        [CommandIds.RevealInExplorer] = "Ctrl+Shift+L",
        [CommandIds.ReopenWithEncoding] = "Ctrl+Shift+D",
        [CommandIds.ChangeEncoding] = "Ctrl+Shift+G",
        [CommandIds.ChangeLineEnding] = "Ctrl+Shift+J",
        [CommandIds.Heading1] = "Ctrl+Shift+1",
        [CommandIds.Heading2] = "Ctrl+Shift+2",
        [CommandIds.Heading3] = "Ctrl+Shift+3",
        [CommandIds.InsertCodeBlock] = "Ctrl+Shift+C",
        [CommandIds.InsertRule] = "Ctrl+Shift+R",
        [CommandIds.InsertTable] = "Ctrl+Shift+4",
        [CommandIds.InsertImage] = "Ctrl+Shift+M",
        [CommandIds.ExportAs] = "Ctrl+Shift+E",
        [CommandIds.About] = "Shift+F1",
        [CommandIds.PastePlain] = "Ctrl+Shift+V",
        [CommandIds.PasteTable] = "Ctrl+Alt+Shift+Insert",
        [CommandIds.Indent] = "Ctrl+OemCloseBrackets",
        [CommandIds.Outdent] = "Ctrl+OemOpenBrackets",
        [CommandIds.TableInsertRowAbove] = "Ctrl+Alt+F4",
        [CommandIds.TableInsertRowBelow] = "Ctrl+Alt+F5",
        [CommandIds.TableInsertColumnLeft] = "Ctrl+Alt+F6",
        [CommandIds.TableInsertColumnRight] = "Ctrl+Alt+F7",
        [CommandIds.TableDeleteRow] = "Ctrl+Alt+F8",
        [CommandIds.TableDeleteColumn] = "Ctrl+Alt+Shift+Home",
        [CommandIds.TableMoveColumnLeft] = "Ctrl+Alt+Shift+Left",
        [CommandIds.TableMoveColumnRight] = "Ctrl+Alt+Shift+Right",
        [CommandIds.TableAlignLeft] = "Ctrl+Alt+Home",
        [CommandIds.TableAlignCenter] = "Ctrl+Alt+Shift+End",
        [CommandIds.TableAlignRight] = "Ctrl+Alt+End",
        [CommandIds.TableAlignNone] = "Ctrl+Alt+Shift+PageUp",
        [CommandIds.TableSortAscending] = "Ctrl+Alt+PageUp",
        [CommandIds.TableSortDescending] = "Ctrl+Alt+PageDown",
        [CommandIds.TableFormat] = "Ctrl+Alt+Shift+PageDown",
    };

    // Defaults replaced by WP10 (AltGr collisions, Alt+Space system menu). A saved binding equal to the
    // former default means the user never chose it, so it follows the new default.
    private static readonly IReadOnlyDictionary<string, string> LegacyDefaultGestures = new Dictionary<string, string>
    {
        [CommandIds.LibraryFiles] = "Ctrl+Alt+Shift+1",
        [CommandIds.LibraryRecent] = "Ctrl+Alt+Shift+2",
        [CommandIds.LibrarySearch] = "Ctrl+Alt+Shift+3",
        [CommandIds.LibraryBacklinks] = "Ctrl+Alt+Shift+4",
        [CommandIds.LibraryBrokenLinks] = "Ctrl+Alt+Shift+5",
        [CommandIds.LibraryTasks] = "Ctrl+Alt+Shift+6",
        [CommandIds.CreateWorkspace] = "Ctrl+Alt+Shift+W",
        [CommandIds.CloseAllWorkspaces] = "Ctrl+Alt+Shift+Q",
        [CommandIds.AskAI] = "Alt+Space",
        [CommandIds.Reset] = "Ctrl+Alt+Shift+Back",
        [CommandIds.ClearRecent] = "Ctrl+Alt+Shift+H",
        [CommandIds.NewWorkspaceFile] = "Ctrl+Alt+Shift+F",
        [CommandIds.NewFolder] = "Ctrl+Alt+Shift+N",
        [CommandIds.ReopenWithEncoding] = "Ctrl+Alt+Shift+O",
        [CommandIds.ChangeEncoding] = "Ctrl+Alt+Shift+C",
        [CommandIds.ChangeLineEnding] = "Ctrl+Alt+Shift+L",
        [CommandIds.InsertRule] = "Ctrl+Alt+Shift+R",
        [CommandIds.InsertTable] = "Ctrl+Alt+Shift+T",
        [CommandIds.SaveAll] = "Ctrl+Alt+S",
        [CommandIds.CloseDocuments] = "Ctrl+Alt+W",
        [CommandIds.ShowHistory] = "Ctrl+Alt+H",
        [CommandIds.ToggleReference] = "Ctrl+Alt+2",
        [CommandIds.ToggleRightPanel] = "Ctrl+Alt+B",
        [CommandIds.ToggleTheme] = "Ctrl+Alt+T",
        [CommandIds.HeadingUp] = "Ctrl+Alt+Up",
        [CommandIds.HeadingDown] = "Ctrl+Alt+Down",
        [CommandIds.PasteImage] = "Ctrl+Alt+V",
    };
    private readonly Dictionary<string, CommandDefinition> _commands = [];
    private readonly Dictionary<string, string> _overrides = new(StringComparer.Ordinal);

    public CommandRegistry(IEnumerable<CommandDefinition> commands)
    {
        foreach (var command in commands)
        {
            if (!_commands.TryAdd(command.Id, command))
            {
                throw new ArgumentException($"Duplicate command id {command.Id}.", nameof(commands));
            }
        }
    }

    public IReadOnlyCollection<CommandDefinition> Commands => _commands.Values;

    public bool TryGet(string id, out CommandDefinition command) => _commands.TryGetValue(id, out command!);

    /// <summary>Loads overrides from settings, silently dropping entries that are invalid, unknown or would clash.</summary>
    public IReadOnlyList<string> LoadOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        // The two former export entry points now share one dialog and one shortcut.
        // An explicit new binding wins; otherwise preserve the former PDF choice, then HTML.
        if (!overrides.ContainsKey(CommandIds.ExportAs)
            && (overrides.TryGetValue(CommandIds.ExportPdf, out var oldExport) || overrides.TryGetValue(CommandIds.ExportHtml, out oldExport)))
        {
            var migrated = overrides.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            migrated[CommandIds.ExportAs] = oldExport;
            migrated.Remove(CommandIds.ExportPdf); migrated.Remove(CommandIds.ExportHtml);
            overrides = migrated;
        }
        overrides = overrides.Where(pair => !(LegacyDefaultGestures.TryGetValue(pair.Key, out var legacy)
                && Gesture.TryParse(pair.Value, out var saved) && Gesture.TryParse(legacy, out var old) && saved == old))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        _overrides.Clear();
        var rejected = new List<string>();
        // Release all valid saved assignments first, so moves and swaps do not
        // conflict with defaults that another saved assignment will replace.
        foreach (var (id, text) in overrides)
            if (_commands.ContainsKey(id) && (string.IsNullOrEmpty(text) || Gesture.TryParse(text, out _)))
                _overrides[id] = string.Empty;
        foreach (var (id, text) in overrides)
        {
            if (!_commands.ContainsKey(id) || !Gesture.TryParse(text, out var saved)) continue;
            foreach (var (addedId, addedText) in AdditionalDefaultGestures)
                if (addedId != id && !overrides.ContainsKey(addedId) && _commands.ContainsKey(addedId)
                    && Gesture.TryParse(addedText, out var added) && saved == added)
                    _overrides[addedId] = string.Empty;
            foreach (var changedId in LegacyDefaultGestures.Keys)
                if (changedId != id && !overrides.ContainsKey(changedId) && _commands.TryGetValue(changedId, out var changed)
                    && Gesture.TryParse(changed.DefaultGesture, out var replacement) && saved == replacement)
                    _overrides[changedId] = string.Empty;
        }
        foreach (var (id, gesture) in overrides.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            // An empty string means the user deliberately unbound the command.
            if (string.IsNullOrEmpty(gesture))
            {
                if (_commands.ContainsKey(id))
                {
                    _overrides[id] = string.Empty;
                }

                continue;
            }

            if (!SetBinding(id, gesture).Succeeded)
            {
                rejected.Add(id);
            }
        }

        // Restore rejected commands only when their defaults are still free.
        foreach (var id in rejected)
            if (_commands.ContainsKey(id)) ResetBinding(id);

        return rejected;
    }

    public IReadOnlyDictionary<string, string> Overrides => _overrides;

    public string? GetGesture(string id)
    {
        if (_overrides.TryGetValue(id, out var overridden))
        {
            return overridden.Length == 0 ? null : overridden;
        }

        return _commands.TryGetValue(id, out var command) ? command.DefaultGesture : null;
    }

    public BindingResult SetBinding(string id, string gestureText)
    {
        if (!_commands.ContainsKey(id))
        {
            return new BindingResult(BindingProblem.UnknownCommand);
        }

        if (!Gesture.TryParse(gestureText, out var gesture))
        {
            return new BindingResult(BindingProblem.Invalid);
        }

        var owner = FindCommandFor(gesture);
        if (owner is not null && owner != id)
        {
            return new BindingResult(BindingProblem.Conflict, owner);
        }

        _overrides[id] = gesture.ToString();
        return new BindingResult(BindingProblem.None);
    }

    /// <summary>Moves a gesture to <paramref name="id"/>, unbinding whichever command held it. Used after the user confirms a conflict.</summary>
    public void ForceBinding(string id, string gestureText)
    {
        if (!Gesture.TryParse(gestureText, out var gesture) || !_commands.ContainsKey(id))
        {
            return;
        }

        var owner = FindCommandFor(gesture);
        if (owner is not null && owner != id)
        {
            _overrides[owner] = string.Empty;
        }

        _overrides[id] = gesture.ToString();
    }

    /// <summary>Leaves a command without any shortcut, even if it has a default.</summary>
    public void ClearBinding(string id)
    {
        if (_commands.ContainsKey(id))
        {
            _overrides[id] = string.Empty;
        }
    }

    public BindingResult ResetBinding(string id)
    {
        if (!_commands.TryGetValue(id, out var command))
            return new BindingResult(BindingProblem.UnknownCommand);
        if (Gesture.TryParse(command.DefaultGesture, out var gesture)
            && FindCommandFor(gesture) is { } owner && owner != id)
            return new BindingResult(BindingProblem.Conflict, owner);
        _overrides.Remove(id);
        return new BindingResult(BindingProblem.None);
    }

    public void ResetAll() => _overrides.Clear();

    public string? FindCommandFor(Gesture gesture)
    {
        foreach (var command in _commands.Values)
        {
            if (Gesture.TryParse(GetGesture(command.Id), out var bound) && bound == gesture)
            {
                return command.Id;
            }
        }

        return null;
    }

    public static CommandRegistry CreateDefault() => new(DefaultCommands());

    public static IEnumerable<CommandDefinition> DefaultCommands() => CoreCommands().Select(command =>
        command.DefaultGesture is null && AdditionalDefaultGestures.TryGetValue(command.Id, out var gesture)
            ? command with { DefaultGesture = gesture } : command);

    private static IEnumerable<CommandDefinition> CoreCommands() =>
    [
        new(CommandIds.LibraryFiles, "Show All Files", "Library"),
        new(CommandIds.CreateWorkspace, "Create Workspace…", "Workspace"),
        new(CommandIds.CloseAllWorkspaces, "Close All Workspaces", "Workspace"),
        new(CommandIds.LibraryRecent, "Show Recent Files", "Library"),
        new(CommandIds.LibrarySearch, "Show Search Results", "Library"),
        new(CommandIds.LibraryBacklinks, "Show Backlinks", "Library"),
        new(CommandIds.LibraryBrokenLinks, "Show Broken Links", "Library"),
        new(CommandIds.LibraryTasks, "Show Tasks", "Library"),
        new(CommandIds.LibraryTags, "Show Tags", "Library"),
        new(CommandIds.LibraryMentions, "Show Unlinked Mentions", "Library"),
        new(CommandIds.LibraryGraph, "Show Local Graph", "Library"),
        new(CommandIds.FoldSection, "Fold or Unfold Section", "Format"),
        new(CommandIds.MoveSectionUp, "Move Section Up", "Format"),
        new(CommandIds.MoveSectionDown, "Move Section Down", "Format"),
        new(CommandIds.PromoteSection, "Promote Section", "Format"),
        new(CommandIds.DemoteSection, "Demote Section", "Format"),
        new(CommandIds.WorkspacePrivacy, "Change Workspace Privacy…", "Workspace"),
        new(CommandIds.ClearFileHistory, "Clear History for This File…", "File"),
        new(CommandIds.ClearWorkspaceHistory, "Clear History for This Workspace…", "Workspace"),
        new(CommandIds.RecentlyDiscarded, "Recently Discarded Drafts…", "File"),
        new(CommandIds.GitDiff, "Compare with Last Git Commit…", "File"),
        new(CommandIds.Help, "Learn Tern…", "Help", "F1"),
        new(CommandIds.AIConnections, "AI Connections…", "Settings"),
        new(CommandIds.AskAI, "Write with AI…", "Edit"),
        new(CommandIds.AskKnowledge, "Explore your notes", "Edit"),
        new(CommandIds.Reset, "Reset Tern…", "App"),
        new(CommandIds.ClearRecent, "Clear Recent History…", "File"),
        new(CommandIds.Rename, "Rename Document…", "File", "F2"),
        new(CommandIds.MoveDocument, "Move Document…", "File"),
        new(CommandIds.DuplicateDocument, "Duplicate Document", "File"),
        new(CommandIds.NewFile, "New Document", "File", "Ctrl+N"),
        new(CommandIds.NewFromTemplate, "New from Template…", "File", "Ctrl+Shift+I"),
        new(CommandIds.OpenTodaysNote, "Open Today's Note", "File", "Ctrl+Shift+0"),
        new(CommandIds.OpenFile, "Open Files…", "File", "Ctrl+O"),
        new(CommandIds.OpenUrl, "Open from URL…", "File"),
        new(CommandIds.NewWorkspaceFile, "New File in Folder…", "File"),
        new(CommandIds.NewFolder, "New Folder…", "File"),
        new(CommandIds.Print, "Print Preview…", "File", "Ctrl+P"),
        new(CommandIds.Undo, "Undo", "Edit", "Ctrl+Z"),
        new(CommandIds.Redo, "Redo", "Edit", "Ctrl+Y"),
        new(CommandIds.Cut, "Cut", "Edit", "Ctrl+X"),
        new(CommandIds.Copy, "Copy", "Edit", "Ctrl+C"),
        new(CommandIds.Paste, "Paste", "Edit", "Ctrl+V"),
        new(CommandIds.PastePlain, "Paste Plain Text", "Edit"),
        new(CommandIds.PasteTable, "Paste as Table", "Edit"),
        new(CommandIds.SelectAll, "Select All", "Edit", "Ctrl+A"),
        new(CommandIds.OpenFolder, "Open Folder…", "File", "Ctrl+Shift+O"),
        new(CommandIds.OpenWorkspace, "Open Workspace…", "Workspace"),
        new(CommandIds.PromoteFolder, "Make Folder a Workspace…", "Workspace"),
        new(CommandIds.Save, "Save", "File", "Ctrl+S"),
        new(CommandIds.SaveAs, "Save As…", "File", "Ctrl+Shift+S"),
        new(CommandIds.SaveAll, "Save All", "File", "Ctrl+Shift+F10"),
        new(CommandIds.CloseWorkspace, "Close Workspace", "File", "Ctrl+Shift+W"),
        new(CommandIds.CloseDocuments, "Close All Documents", "File", "Ctrl+Shift+F11"),
        new(CommandIds.CloseTab, "Close Tab", "File", "Ctrl+W"),
        new(CommandIds.ReopenClosedTab, "Reopen Closed Tab", "File", "Ctrl+Shift+T"),
        new(CommandIds.ReloadFromDisk, "Reload from Disk…", "File"),
        new(CommandIds.RevealInExplorer, "Reveal in File Explorer", "File"),
        new(CommandIds.ReviewSaveFragments, "Recovered Save Fragments…", "File"),
        new(CommandIds.ReopenWithEncoding, "Reopen with Encoding…", "File"),
        new(CommandIds.ChangeEncoding, "Change Save Encoding…", "File"),
        new(CommandIds.ChangeLineEnding, "Change Line Endings…", "File"),
        new(CommandIds.NextTab, "Next Tab", "Navigate", "Ctrl+Tab"),
        new(CommandIds.PreviousTab, "Previous Tab", "Navigate", "Ctrl+Shift+Tab"),
        new(CommandIds.QuickOpen, "Quick Open…", "Navigate", "Ctrl+K"),
        new(CommandIds.CommandPalette, "Command Palette…", "Navigate", "Ctrl+Shift+P"),
        new(CommandIds.GoToLine, "Go to Line…", "Navigate", "Ctrl+G"),
        new(CommandIds.GoToHeading, "Go to Heading…", "Navigate", "Ctrl+Shift+H"),
        new(CommandIds.Find, "Find…", "Edit", "Ctrl+F"),
        new(CommandIds.Replace, "Replace…", "Edit", "Ctrl+H"),
        new(CommandIds.FindInWorkspace, "Find in Workspace…", "Search", "Ctrl+Shift+F"),
        new(CommandIds.ReplaceInWorkspace, "Replace in Workspace…", "Search"),
        new(CommandIds.ViewSource, "Source View", "View", "Ctrl+1"),
        new(CommandIds.ViewSplit, "Split View", "View", "Ctrl+2"),
        new(CommandIds.ViewReading, "Reading View", "View", "Ctrl+3"),
        new(CommandIds.ShowHistory, "Version History…", "File", "Ctrl+Shift+Y"),
        new(CommandIds.ToggleReference, "Reference Pane", "View", "Ctrl+Shift+5"),
        new(CommandIds.ResetLayout, "Reset Layout", "View", "Ctrl+Alt+Shift+F10"),
        new(CommandIds.Welcome, "Tern Quick Start…", "Help", "Ctrl+Alt+Shift+F12"),
        new(CommandIds.ToggleFocus, "Focus Mode", "View", "F8"),
        new(CommandIds.ToggleSidebar, "Library", "View", "Ctrl+Shift+B"),
        new(CommandIds.ToggleRightPanel, "Inspector", "View", "Ctrl+Shift+6"),
        new(CommandIds.ToggleTheme, "Switch Theme", "View", "Ctrl+Shift+F12"),
        new(CommandIds.ZoomIn, "Zoom In", "View", "Ctrl+OemPlus"),
        new(CommandIds.ZoomOut, "Zoom Out", "View", "Ctrl+OemMinus"),
        new(CommandIds.ZoomReset, "Reset Zoom", "View", "Ctrl+D0"),
        new(CommandIds.ReadCurrentLine, "Read Current Line", "View", "Ctrl+Alt+Shift+F1"),
        new(CommandIds.ReadDocumentStatus, "Read Document Status", "View", "Ctrl+Alt+Shift+F2"),
        new(CommandIds.Bold, "Bold", "Format", "Ctrl+B"),
        new(CommandIds.Italic, "Italic", "Format", "Ctrl+I"),
        new(CommandIds.InlineCode, "Inline Code", "Format", "Ctrl+E"),
        new(CommandIds.Strikethrough, "Strikethrough", "Format", "Ctrl+Shift+X"),
        new(CommandIds.InsertLink, "Insert Link…", "Format", "Ctrl+Shift+K"),
        new(CommandIds.Heading1, "Heading 1 (Title)", "Format"),
        new(CommandIds.Heading2, "Heading 2 (Section)", "Format"),
        new(CommandIds.Heading3, "Heading 3 (Subsection)", "Format"),
        new(CommandIds.HeadingUp, "Larger Heading", "Format", "Ctrl+Alt+Shift+Up"),
        new(CommandIds.HeadingDown, "Smaller Heading", "Format", "Ctrl+Alt+Shift+Down"),
        new(CommandIds.ToggleQuote, "Quote", "Format", "Ctrl+Shift+Q"),
        new(CommandIds.ToggleBullet, "Bullet List", "Format", "Ctrl+Shift+8"),
        new(CommandIds.ToggleNumbered, "Numbered List", "Format", "Ctrl+Shift+7"),
        new(CommandIds.ToggleTask, "Checklist", "Format", "Ctrl+Shift+9"),
        new(CommandIds.ToggleTaskAtCaret, "Toggle Task at Cursor", "Format", "Ctrl+Enter"),
        new(CommandIds.Indent, "Indent", "Format"),
        new(CommandIds.Outdent, "Outdent", "Format"),
        new(CommandIds.TableInsertRowAbove, "Insert Row Above", "Format"),
        new(CommandIds.TableInsertRowBelow, "Insert Row Below", "Format"),
        new(CommandIds.TableInsertColumnLeft, "Insert Column Left", "Format"),
        new(CommandIds.TableInsertColumnRight, "Insert Column Right", "Format"),
        new(CommandIds.TableDeleteRow, "Delete Row", "Format"),
        new(CommandIds.TableDeleteColumn, "Delete Column", "Format"),
        new(CommandIds.TableMoveColumnLeft, "Move Column Left", "Format"),
        new(CommandIds.TableMoveColumnRight, "Move Column Right", "Format"),
        new(CommandIds.TableAlignLeft, "Align Column Left", "Format"),
        new(CommandIds.TableAlignCenter, "Align Column Center", "Format"),
        new(CommandIds.TableAlignRight, "Align Column Right", "Format"),
        new(CommandIds.TableAlignNone, "Clear Column Alignment", "Format"),
        new(CommandIds.TableSortAscending, "Sort Rows Ascending", "Format"),
        new(CommandIds.TableSortDescending, "Sort Rows Descending", "Format"),
        new(CommandIds.TableFormat, "Format Table", "Format"),
        new(CommandIds.InsertCodeBlock, "Insert Code Block", "Insert"),
        new(CommandIds.InsertRule, "Insert Horizontal Rule", "Insert"),
        new(CommandIds.InsertTable, "Insert Table…", "Insert"),
        new(CommandIds.InsertImage, "Insert Local Image…", "Insert"),
        new(CommandIds.PasteImage, "Paste Image as Attachment…", "Insert", "Ctrl+Alt+Shift+F3"),
        new(CommandIds.ExportAs, "Export As…", "File"),
        new(CommandIds.Settings, "Settings…", "App", "Ctrl+OemComma"),
        new(CommandIds.Shortcuts, "Keyboard Shortcuts…", "App", "Ctrl+OemQuestion"),
        new(CommandIds.About, "About Tern…", "App"),
        new(CommandIds.Exit, "Exit", "App", "Alt+F4"),
    ];
}
