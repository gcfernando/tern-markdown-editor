using Tern.App.Commands;

namespace Tern.App.Help;

public sealed record FeatureHelp(string Id, string Name, string Category, string Description, string Prerequisite, string Shortcut, string? LaunchCommandId = null, IReadOnlyList<string>? TutorialSteps = null, string PaletteShortcut = "", string? AdvancedDetails = null, string? MenuPath = null)
{
    public IReadOnlyList<string> Steps => TutorialSteps ??
    [
        $"Before you start: {Prerequisite} This guide never performs actions on your documents.",
        (MenuPath is not null ? $"Choose {MenuPath}.{(Shortcut.Length > 0 ? $" Current shortcut: {Shortcut}." : "") } Alternatively, " : "Open ") + $"Help → All Commands{(PaletteShortcut.Length > 0 ? $" ({PaletteShortcut})" : "")} and search for {Name}.",
        Description,
        "Check the result before saving or replacing anything. You can leave this guide, replay it, or select another feature at any time.",
    ];
}
public sealed record Tutorial(string Name, IReadOnlyList<string> Steps, string? LaunchCommandId = null);

/// <summary>Help uses the same command names and effective shortcuts as the application.</summary>
public static partial class FeatureCatalog
{
    public static bool CanLaunchFromGuide(string? id) => id is CommandIds.Settings or CommandIds.Shortcuts or CommandIds.AIConnections or CommandIds.OpenFolder or CommandIds.OpenFile or CommandIds.OpenUrl or CommandIds.NewFile
        or CommandIds.LibraryFiles or CommandIds.LibraryRecent or CommandIds.LibrarySearch or CommandIds.LibraryBacklinks or CommandIds.LibraryBrokenLinks or CommandIds.LibraryTasks
        or CommandIds.ViewSource or CommandIds.ViewSplit or CommandIds.ViewReading or CommandIds.Find or CommandIds.GoToLine or CommandIds.GoToHeading
        or CommandIds.CreateWorkspace or CommandIds.OpenWorkspace;
    public static IReadOnlyList<FeatureHelp> Search(CommandRegistry registry, string? query)
    {
        var words = GuideQuery.Words(query);
        return registry.Commands.Select(c => new FeatureHelp(c.Id, c.Title, c.Category, Explain(c), Prerequisite(c), Gesture.Describe(registry.GetGesture(c.Id)), c.Id, MenuPath: MenuLocation(c)))
            .Concat(Subfeatures)
            .Select(f => f with { PaletteShortcut = Gesture.Describe(registry.GetGesture(CommandIds.CommandPalette)),
                TutorialSteps = f.TutorialSteps?.Select(step => step.Replace("Alt+Space", WritingShortcut(registry), StringComparison.Ordinal)).ToArray(),
                Description = f.Description.Replace("Alt+Space", WritingShortcut(registry), StringComparison.Ordinal) })
            .Where(f => GuideQuery.Matches($"{f.Name} {f.Category} {f.Description} {f.Prerequisite} {f.AdvancedDetails}", words))
            .OrderBy(f => f.Category, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string WritingShortcut(CommandRegistry registry) => Gesture.Describe(registry.GetGesture(CommandIds.AskAI)) is { Length: > 0 } shortcut
        ? shortcut : "Edit → Write with AI";

    private static string? MenuLocation(CommandDefinition command) => command.Id switch
    {
        CommandIds.NewFile => "File → New Document",
        CommandIds.OpenFile => "File → Open Files…",
        CommandIds.OpenUrl => "File → Open from URL…",
        CommandIds.OpenFolder => "File → Open Folder…",
        CommandIds.OpenWorkspace => "File → Open Workspace…",
        CommandIds.CreateWorkspace => "File → Create Workspace…",
        CommandIds.Save => "File → Save",
        CommandIds.SaveAs => "File → Save As…",
        CommandIds.SaveAll => "File → Save All",
        CommandIds.CloseTab => "File → Close Tab",
        CommandIds.CloseDocuments => "File → Close All Documents",
        CommandIds.CloseWorkspace => "File → Close Workspace",
        CommandIds.CloseAllWorkspaces => "File → Close All Workspaces",
        CommandIds.ExportPdf => "File → Export as PDF…",
        CommandIds.ExportHtml => "File → Export as HTML…",
        CommandIds.Print => "File → Print…",
        CommandIds.Rename => "File → Rename Document…",
        CommandIds.Find => "Edit → Find…",
        CommandIds.Replace => "Edit → Replace…",
        CommandIds.FindInWorkspace => "Edit → Find in Workspace…",
        CommandIds.Settings => "Edit → Settings…",
        CommandIds.ViewSource => "View → Source",
        CommandIds.ViewSplit => "View → Split",
        CommandIds.ViewReading => "View → Reading",
        CommandIds.CommandPalette => "Help → All Commands…",
        CommandIds.Shortcuts => "Help → Keyboard Shortcuts…",
        CommandIds.Help => "Help → Feature Guide and Tutorials…",
        CommandIds.AIConnections => "Edit → Settings → AI connections",
        CommandIds.AskAI => "Edit → Write with AI…",
        CommandIds.About => "Help → About Tern…",
        CommandIds.Reset => "Help → Reset Tern…",
        CommandIds.ToggleQuote => "Format → Quote",
        CommandIds.ToggleBullet => "Format → Bullet List",
        CommandIds.ToggleNumbered => "Format → Numbered List",
        CommandIds.ToggleTask => "Format → Checklist",
        CommandIds.ToggleTaskAtCaret => null,
        CommandIds.InsertLink => "Format → Insert Link",
        CommandIds.Heading1 or CommandIds.Heading2 or CommandIds.Heading3 => "Format → Heading → " + command.Title,
        CommandIds.InsertImage => "Format → Insert Local Image…",
        CommandIds.InsertTable => "Format → Insert Table",
        CommandIds.InsertCodeBlock => "Format → Insert Code Block",
        CommandIds.InsertRule => "Format → Insert Horizontal Rule",
        CommandIds.PasteImage => "Format → Paste Image as Attachment…",
        _ when command.Category == "Format" => "Format → " + command.Title,
        _ => null,
    };

    private static string Prerequisite(CommandDefinition c) => c.Id switch
    {
        CommandIds.OpenUrl => "Use a public HTTP or HTTPS Markdown/plain-text address or public GitHub file link with an internet connection. A server on this computer is also supported: start it first and use localhost, 127.0.0.1 or ::1. Sign-in-only files and other private network addresses are not supported.",
        CommandIds.LibraryRecent => "Available without an open document or workspace. Previously opened workspaces, saved files and URL references appear here.",
        CommandIds.LibraryFiles => "Open a workspace to browse its file tree. Without one, this shows the workspace-free Library state.",
        CommandIds.LibrarySearch => "Open a workspace and run a workspace search before revisiting its results.",
        CommandIds.LibraryBacklinks => "Open a workspace and a saved document within it to inspect incoming links.",
        CommandIds.LibraryBrokenLinks or CommandIds.LibraryTasks => "Open a workspace first. Results depend on its links or task-list markup.",
        CommandIds.Help or CommandIds.Settings or CommandIds.Shortcuts or CommandIds.About or CommandIds.Reset or CommandIds.ClearRecent => "Available without an open document or workspace.",
        CommandIds.AIConnections => "Optional. Start a compatible local server or obtain a cloud endpoint, model name and any required API key. Core editing needs no connection.",
        CommandIds.AskAI => "Open a writable note and save an AI connection under Edit → Settings → AI connections. Choose the connection in the main toolbar.",
        CommandIds.Rename => "Open a local file or a new local draft. A saved file requires permission to rename its path; read-only text can still be renamed. Download an online copy to a local file first.",
        CommandIds.ReloadFromDisk or CommandIds.RevealInExplorer or CommandIds.ShowHistory => "Open a saved document first. Version history contains only versions previously recorded by Tern.",
        CommandIds.ReopenWithEncoding => "Open a saved document with no unsaved changes. Save or discard edits first; this rereads existing bytes rather than changing save encoding.",
        CommandIds.Save => "Open a writable document containing unsaved changes, or a saved document whose missing file needs recreation. An untouched empty new document and a clean saved document do not need Save.",
        CommandIds.SaveAs => "Open a writable document. Even an empty new document can be saved with Save As. Choose a separate path when a copy is wanted.",
        CommandIds.SaveAll => "At least one open document must have unsaved changes, and no document operation may be in progress.",
        CommandIds.Undo => "Open a writable document with at least one undoable edit.",
        CommandIds.Redo => "Open a writable document with at least one undone edit to reapply.",
        CommandIds.NextTab or CommandIds.PreviousTab => "Open at least two documents so there is another tab to activate.",
        CommandIds.ViewSource or CommandIds.ViewSplit or CommandIds.ViewReading or CommandIds.GoToLine or CommandIds.GoToHeading or CommandIds.CloseTab or CommandIds.CloseDocuments or CommandIds.ToggleReference => "Open or create a document first. Navigation and reading also work for read-only documents.",
        CommandIds.NewWorkspaceFile or CommandIds.NewFolder or CommandIds.CloseWorkspace => "Open a folder or workspace first. Creation targets the selected root.",
        CommandIds.FindInWorkspace => "Open a saved workspace first. Open Folder alone provides browsing, not workspace content search.",
        CommandIds.OpenWorkspace => "Choose the root directory containing .tern-workspace.json, or an existing legacy workspace.",
        CommandIds.CreateWorkspace => "Choose a writable parent location and an unused workspace name. Use Open Workspace to restore saved workspace metadata, or Open Folder to browse an ordinary directory.",
        CommandIds.CloseAllWorkspaces => "At least one workspace must be open. Review unsaved-document decisions before detaching all project roots.",
        CommandIds.Replace or CommandIds.ChangeLineEnding or CommandIds.ChangeEncoding => "Open a writable document and wait for any loading or saving operation to finish.",
        CommandIds.Print or CommandIds.ExportHtml or CommandIds.ExportPdf => "Open an existing file, or write non-blank text in a new note. Wait for any document operation to finish. Opened files count without edits; read-only files can also be exported or printed.",
        CommandIds.Find => "Open or create a document first and wait for any document operation to finish.",
        CommandIds.InsertImage or CommandIds.PasteImage => "Choose a writable note; a new note asks where to save before adding pictures. Return to Source or Split and choose the insertion point.",
        _ when c.Category is "Format" or "Insert" => "Open a writable document first and wait for any loading or saving operation to finish.",
        _ => "Availability follows the current document and workspace state.",
    };

    private static string Explain(CommandDefinition c) => c.Id switch
    {
        CommandIds.LibraryFiles => "Show All Files in the Library to browse the workspace folder tree, expand folders and open documents.",
        CommandIds.LibraryRecent => "Show Recent workspaces, saved files and URLs in the Library. Click a workspace to restore its saved working context; Remove from Recent forgets only the reference.",
        CommandIds.LibrarySearch => "Show the latest workspace Search results in the Library. Run Search in Workspace to enter a new text query.",
        CommandIds.LibraryBacklinks => "Show workspace documents that link to the active saved document. Select a result to inspect the referring source.",
        CommandIds.LibraryBrokenLinks => "Show unresolved workspace links in the Library. Select a result to inspect its source and intended target before repair.",
        CommandIds.LibraryTasks => "Scan and show task-list items from workspace documents. Select a task to navigate to its source line.",
        CommandIds.Help => "Search feature names, meanings and prerequisites. Choose a beginner tutorial, move Back or Next, replay it, or exit at any time. Tutorials never edit documents automatically.",
        CommandIds.AIConnections => "Open Edit → Settings → AI connections → Manage AI connections. Follow Choose service, Configure connection, then Validate and save. Validate sends a fixed test message using the selected model; errors explain what to correct. Save stores settings and protects keys in Windows Credential Manager. The AI service menu includes GitHub Copilot and MCP server alongside APIs. Create, modify, delete and validate saved connections here. All appear in the main toolbar: API and Copilot offer Write with AI; MCP offers Open MCP tools. Website subscriptions do not imply API access.",
        CommandIds.AskAI => "Open the multiline AI writing panel for the active note. Choose a saved connection, describe a change and select Generate & update note. The question and current Markdown are sent to that service; the result replaces the active draft as one undoable edit. Nothing is saved to disk automatically.",
        CommandIds.Reset => "Select recent history, preferences, session restore state, search indexes or cached online documents/assets. Close all workspaces before clearing indexes, and close cached online tabs before clearing their cache. Open drafts, physical files, attachments, recovery copies and separately managed AI credentials are preserved.",
        CommandIds.ClearRecent => "Remove recent and pinned history references from Recent, standalone Library suggestions and Quick Open history. Explicit workspace files and open document tabs remain; physical documents are never deleted.",
        CommandIds.Rename => "Name a new local draft without creating a file, or rename a saved local file within its current folder. Right-click the tab and choose Rename, or use File → Rename Document…. Online copies must be downloaded first. Invalid names and collisions are refused. Review unsaved changes and save explicitly.",
        CommandIds.NewWorkspaceFile => "Create a Markdown file in the open workspace folder. Enter a valid unused filename; existing files are not overwritten.",
        CommandIds.NewFolder => "Create a folder within the open workspace. Enter a valid unused folder name.",
        CommandIds.CreateWorkspace => "Create a new directory and a .tern-workspace.json descriptor containing its name and format version. Existing directories are not overwritten. Use Open Workspace to restore a saved context or Open Folder to browse a plain directory.",
        CommandIds.CloseAllWorkspaces => "Close all workspace roots after the complete set of unsaved-change decisions. Standalone and unnamed documents remain open; source files and folders remain on disk.",
        CommandIds.ReopenClosedTab => "Reopen a recently closed saved document when its path is still available. Closing unsaved edits is not a way to back them up.",
        CommandIds.NextTab => "Activate the next open document tab without saving or closing the current document.",
        CommandIds.PreviousTab => "Activate the previous open document tab without saving or closing the current document.",
        CommandIds.GoToLine => "Enter a line number to move the source editor caret to that line.",
        CommandIds.GoToHeading => "Search headings in the current document and choose one to navigate to its position.",
        CommandIds.ToggleSidebar => "Show or hide the Library panel containing workspace files and recent document references.",
        CommandIds.ToggleRightPanel => "Show or hide the inspector containing document headings, links, statistics, tags and version history.",
        CommandIds.ToggleTheme => "Switch between the light Paper and dark Midnight appearance. Settings also offers the Windows system theme.",
        CommandIds.InsertTable => "Choose 1–100 rows including the header and 1–20 columns. Insert creates Markdown at the caret or replaces the selection as one undoable edit. Cancel keeps the text. Edit headings and cells in Source or Split.",
        CommandIds.InsertCodeBlock => "Put selected text in a fenced code block, or insert an empty block and type inside it. The preview shows the contents as literal code. Undo reverses the insertion.",
        CommandIds.InsertRule => "Insert a horizontal dividing line on its own line. Blank lines keep it separate from nearby paragraphs and headings. Undo reverses the insertion.",
        CommandIds.About => "Show the Tern version, offline behavior and third-party component license notices.",
        CommandIds.Exit => "Exit Tern after reviewing unsaved documents. Save keeps edits, Discard abandons them, and Cancel keeps the application open.",
        CommandIds.NewFile => "Start a new document in memory. Choose Save to give it a filename; a workspace is optional.",
        CommandIds.OpenFile => "Choose Markdown files (.md, .markdown, .mdown) or text notes (.txt) from your computer and open each in a tab. Text notes remain editable and use the Markdown preview. The first successful open replaces the initial New document. If it contains edits, review Save, Don't save or Cancel first. A failed open keeps your note.",
        CommandIds.OpenWorkspace => "Open a saved named workspace root containing .tern-workspace.json, populate its tree and restore saved-document tabs, caret/scroll positions, active document and view mode recorded on a successful workspace close. Unsaved text stays in recovery, not workspace metadata. Open Folder only browses; legacy roots remain supported.",
        CommandIds.OpenUrl => "Open a Markdown link as an editable, unsaved local copy. Choose + Add → Open from URL… or File → Open from URL…, paste a public GitHub file link, direct raw/download address or address from a running server on this computer, and deliberately choose Open for an online copy or Download and Open to choose a local file. GitHub file links are converted to their raw file automatically. Local servers use localhost, 127.0.0.1 or ::1 over HTTP or HTTPS and must already be running. Opening the tool alone makes no request. Save As creates a local file; the original is never changed. The Markdown and directly referenced supported pictures/files are downloaded together, and their links are rewritten for local use. Open uses the managed cache; Download and Open chooses a destination for a portable copy. Asset failures are reported; Tern does not crawl whole websites.",
        CommandIds.OpenFolder => "Browse a physical directory and its nested files/folders without creating workspace metadata. Use Open Workspace for a saved named context and its workspace tools.",
        CommandIds.Save => "Write changed text with the selected encoding and line endings (LF, CRLF or CR). New documents default to CRLF; opening an existing file preserves its current format. A new document asks for a filename.",
        CommandIds.SaveAs => "Choose another filename and make it this document's saved location. The saved text keeps the selected encoding and LF, CRLF or CR line endings. Check overwrite prompts carefully.",
        CommandIds.SaveAll => "Save changed open documents with each document's selected encoding and line endings, choosing filenames for new documents when asked.",
        CommandIds.CloseWorkspace => "Close the workspace and its documents after Save, Discard or Cancel decisions. Physical files remain on disk.",
        CommandIds.CloseTab or CommandIds.CloseDocuments => "Close document tabs. Save keeps changes, Discard drops unsaved edits, and Cancel keeps the document open.",
        CommandIds.ChangeLineEnding => "Inspect the current line-break convention and choose LF (Unix / macOS), CRLF (Windows) or CR (classic Mac). Save, Save As and Save All preserve the selected line endings. Opening an existing file alone does not convert its text; encoding remains separate.",
        CommandIds.ChangeEncoding => "Choose the character encoding used on the next save. Conversion is refused if characters would be lost.",
        CommandIds.ReopenWithEncoding => "Decode a saved file using another encoding. Save or discard unsaved edits first.",
        CommandIds.Undo => "Reverse the most recent edit in the current document. Saved files change only after saving again.",
        CommandIds.Redo => "Reapply an edit that was undone, when a redo is available.",
        CommandIds.Find => "Search for text within the current document. Use the search controls to move between matches.",
        CommandIds.Replace => "Find matching text and replace a selected match or all matches. Review the search options before replacing.",
        CommandIds.FindInWorkspace => "Search indexed files in the workspace. Results identify the file and matching text.",
        CommandIds.QuickOpen => "Search document filenames; type # to find headings in the selected workspace, or in open saved documents when no workspace is selected. Use the Inspector Outline for the current document.",
        CommandIds.CommandPalette => "Search command names and choose a command. Keyboard shortcuts can be customized separately.",
        CommandIds.ViewSource => "Show editable Markdown source only.",
        CommandIds.ViewSplit => "Show Markdown source beside its rendered preview. Enable synchronized scrolling in Settings to move both panes together.",
        CommandIds.ViewReading => "Show the rendered document with the source pane hidden. Return to Source or Split to edit.",
        CommandIds.ShowHistory => "Inspect locally saved text versions for this document. Restoring creates a separate unsaved copy for review.",
        CommandIds.ToggleReference => "Show another open document as a read-only reference while editing the active document.",
        CommandIds.ToggleFocus => "Hide surrounding panels for focused writing. Press Escape to restore the layout.",
        CommandIds.ExportHtml => "Create an HTML copy of the rendered document. The original Markdown remains unchanged.",
        CommandIds.ExportPdf => "Create a PDF copy for sharing or printing. Review the output because font and layout coverage can vary.",
        CommandIds.Print => "Print readable document text through Windows. Choose PDF export when you need a rendered copy.",
        CommandIds.Settings => "Adjust theme, editor and preview fonts, reading width, reduced motion, word wrap, line numbers, current-line highlighting, tab size and spaces, synchronized scrolling, default view and Markdown profile. File preferences control session restore, external-change reload, final newline, trailing whitespace and external links. Changes apply when you close Settings.",
        CommandIds.Shortcuts => "Search a command, select it, press a new key combination and choose Assign. Conflicting shortcuts require a choice.",
        CommandIds.InsertImage or CommandIds.PasteImage => "Save the note first. Insert Local Image first uses copied PNG, JPEG, GIF, WebP or BMP files from the clipboard. If no copied image file is available, it opens the file picker. For Paste Image as Attachment, copy an image file in File Explorer or copy image pixels, then choose Format → Paste Image as Attachment. Ordinary Ctrl+V in the writing pane can also paste clipboard text; the explicit attachment command needs image data. An image file beside the Markdown file is linked immediately through drag-and-drop, Insert Local Image, Ctrl+V or Paste Image as Attachment. Images from other folders offer Link original or Copy image. Link uses the source path, including subfolders or other locations. Copy asks for a destination and links the new file. Paste Image as Attachment follows the same file rules. Only clipboard pixels without a source file go directly to the copy picker; duplicate names are never overwritten. Check the preview and save the Markdown. Undo removes the link while the copied picture stays on disk.",
        CommandIds.ReloadFromDisk => "Check the file on disk for external changes. Modified buffers require a conflict decision.",
        CommandIds.RevealInExplorer => "Show the saved document's location in Windows File Explorer.",
        CommandIds.Bold => "Make selected text bold with **double asterisks**. Place the caret or select text, then choose Bold; Undo reverses the markup edit.",
        CommandIds.Italic => "Emphasize selected text with *single asterisks*. Place the caret or select text, then choose Italic.",
        CommandIds.InlineCode => "Mark a short code fragment using backticks, such as `name`. Inline code preserves its literal text instead of applying ordinary emphasis.",
        CommandIds.Strikethrough => "Mark text as crossed out using ~~double tildes~~. Preview support depends on the selected Markdown profile.",
        CommandIds.InsertLink => "Wrap selected text as a Markdown link, [label](target). Replace the target with a local path or web address and review it before opening.",
        CommandIds.Heading1 or CommandIds.Heading2 or CommandIds.Heading3 => "Set the line containing the caret (or the start of a selection) to a title, section or subsection heading. Type the heading words after the # marks and check the preview. Undo reverses the edit.",
        CommandIds.HeadingUp => "Make an existing heading larger by removing one #. On a # title, another use returns it to plain text. Place the caret on the heading line first; Undo reverses the edit.",
        CommandIds.HeadingDown => "Make a heading smaller by adding one #, or turn a plain line into a # heading. Place the caret on that line first. The smallest level is ######.",
        CommandIds.ToggleQuote => "Add or remove the > prefix on selected lines to show them as a block quotation.",
        CommandIds.ToggleBullet => "Add or remove an unordered-list marker, such as - item, on selected lines.",
        CommandIds.ToggleNumbered => "Add or remove numbered-list markup on selected lines. The preview displays an ordered list.",
        CommandIds.ToggleTask => "Add or remove task-list markup, such as - [ ] task, on selected lines. Use the task command at the caret to mark a task complete.",
        CommandIds.ToggleTaskAtCaret => "On a task-list line, switch between unchecked [ ] and checked [x]. This edits the Markdown; save explicitly to keep it on disk.",
        _ when c.Category == "Format" => $"Select text or place the caret, then choose {c.Title} to apply the corresponding Markdown markup. Undo reverses the edit.",
        _ when c.Id.StartsWith("view.zoom", StringComparison.Ordinal) => "Change the document text size. Reset returns to the default size.",
        _ => $"Choose {c.Category} → {c.Title} from the menu or command palette. Use the listed shortcut when assigned.",
    };

    public static IReadOnlyList<Tutorial> Tutorials { get; } =
    [
        new("Write your first document", ["You can exit at any time. These instructions never change your documents automatically.", "Close this guide and choose File → New document. No workspace is required.", "Type # My first note, then a blank line and some text. Split shows the preview beside your source.", "Choose File → Save, select a folder and filename, then save. A changed document displays an unsaved-change indicator.", "Replay this guide from Help whenever you need it."]),
        new("Browse and search a workspace", ["A folder is a physical directory. A workspace is a named persistent context for a folder and its application state. Markdown files stay ordinary files. This guide never creates or removes files.", "Choose File → Open Workspace and select a saved workspace containing Markdown files. Open Folder is the separate plain-directory browser.", "Choose a file in the Library. Quick Open searches filenames; Find in Workspace searches workspace text.", "Close Workspace releases the folder and its documents. Review Save, Discard and Cancel prompts; files remain on disk."]),
        new("Change line endings safely", ["Open a document. Keep a copy if another tool requires a particular format.", "Click LF, CRLF or CR in the status bar, or search Change Line Endings in the command palette.", "Choose LF, CRLF or CR to convert the open text. The conversion does not save yet.", "Choose Save to write the file. Save, Save As and Save All preserve your selected line endings. The separate encoding control determines character encoding."]),
        new("Find features and customize keys", ["Help → All Commands opens the command palette. Type a feature name and select the desired command.", "Open Help to search feature descriptions and prerequisites, including LF, CRLF and synchronized scrolling.", "Open Keyboard Shortcuts, search a command, select it, press the desired keys and choose Assign.", "If keys are already assigned, review the conflict prompt. Cancel preserves the existing binding. Reset restores defaults."]),
        new("Why use a workspace?", ["A workspace is a named saved context for related files. Open Folder browses a directory without creating workspace metadata. Workspaces are optional: standalone documents still support opening, writing and saving.", "Use a workspace when you want a folder explorer, project-wide text search, backlinks, broken-link checks and unfinished tasks together.", "Choose Open Folder to browse an existing directory, Open Workspace to restore a saved workspace, or Create Workspace for a named new context. Markdown remains ordinary files.", "You may open multiple roots. Switch the active workspace to focus its file tree and search/link graph; open tabs and unsaved text remain intact.", "Close Workspace detaches the selected root after unsaved-change decisions. Close All Workspaces reviews the open roots together; standalone and unnamed documents remain separate."], CommandIds.OpenFolder),
        new("Write without a workspace", ["You do not need a folder project for a single note. Existing open documents are preserved while you follow this lesson.", "Choose File → New document or Open Files. Type Markdown and use Source, Split or Reading as needed.", "Choose Save and pick the filename and ordinary folder where you want the document. Opening a file does not require attaching that folder as a workspace.", "Add a workspace later if you need its explorer, search, links and tasks. Your document remains an ordinary file."], CommandIds.NewFile),
        new("Open and switch multiple workspaces", ["Use this workflow for separate projects that should keep independent folder trees and search/link graphs.", "Choose Open Workspace for a saved context, or Open Folder to browse an existing directory. Add another root for an independent project; already-open roots are reused.", "Select a workspace or folder in Library. Roots appear as stacked expandable trees, with nested workspaces shown beneath their parents. Expand all and Collapse all apply to Library roots. Switching preserves open tabs and unsaved edits.", "For a new project, choose Create Workspace and enter an unused folder name. Existing folders are opened rather than overwritten.", "Use Close Workspace for the active root or Close All Workspaces for all roots. Review Save, Discard and Cancel for affected documents before detaching anything."], CommandIds.OpenFolder),
        new("Connect a local Ollama model", ["Prerequisites: a running Ollama server and an installed model. Tern does not download or start models. Opening AI Connections sends nothing.", "Open Settings → AI connections → Manage AI connections. On Choose service, select Ollama (local), then Configure connection. Name the connection and enter the exact installed model name; Advanced shows the local /api/chat endpoint.", "Choose Continue to validation. Read the destination, then Validate connection. This sends a fixed test message to the selected model and displays success or an error; it never sends a note.", "If the server cannot be reached, check that Ollama is running and the endpoint is correct. If the model is unavailable, check its exact installed ID. Save connection stores the settings; saving alone does not test them.", "For a separate question, expand Try your own message (optional), paste a short reviewed message and check consent before Send request. Alternatively choose the saved API connection in the main toolbar for writing.", "Review the response before copying it. Cancel request stops waiting but cannot recall text already received. A writing request can send the active note; validation never attaches it."], CommandIds.AIConnections),
        new("Connect a compatible cloud model", ["Prerequisites: API access, a supported protocol, a permitted model ID and required credentials. A website subscription does not necessarily include API access.", "Open Settings → AI connections → Manage AI connections. Choose a matching service, then Configure connection. Enter a connection name, exact model ID and key. Use Advanced for a documented custom HTTPS endpoint; keep secrets out of URLs.", "Choose Continue to validation and review the service, endpoint and model. Validate connection sends the fixed message Reply with exactly: OK. Provider usage or charges may apply; no document is attached.", "Read the result. Authentication errors require checking the key and API permissions; unavailable-model errors require checking the model ID and account access. For connection failures, check the full endpoint, TLS and network. Correct inputs using Back and validate again.", "Choose Save connection to store metadata and protect the key in Windows Credential Manager. Saving does not send a request and is allowed without validation; an untested saved connection has not been proved to work.", "Reopen and select the saved connection to edit it; Save changes updates it. Back to Choose service provides Delete connection and Create connection. Expand Try your own message (optional) for a separately reviewed question and consent before Send request."], CommandIds.AIConnections),
        new("Save a GitHub Copilot connection", ["Have Copilot access and native copilot.exe installed. Create a personal-account-owned fine-grained GitHub token with Copilot Requests permission; Copilot Chat permission is unnecessary.", "Open AI Connections and select GitHub Copilot in the AI service menu. Selecting a saved connection stays on Choose service. Setup guide opens GitHub's official CLI authentication instructions; choose Configure connection when ready. In Configure, name the connection, enter the token, leave Model at auto for a first test, and confirm the CLI location in Advanced.", "Continue to Validate and save. Validate connection sends a fixed test question using the selected CLI, token and model. Read the success or error; provider usage may apply and no note is attached.", "Save connection stores settings and protects the token in Windows Credential Manager. Next time, select the saved connection. Manage saved connections provides New connection and Delete saved connection.", "The saved Copilot connection appears in the main toolbar. Choose Write with AI, enter an instruction, review the exact request and Generate & update note; the active note is included and Undo restores the draft. The complete Copilot request is limited to 8,192 characters and excludes @mentions and CLI commands. For a separate question, use Try connection, review text, check consent and Send reviewed text. Cancel cannot recall text already received."], CommandIds.AIConnections),
        new("Save an MCP tool server", ["Obtain a Streamable HTTP endpoint with MCP 2025-11-25 compatibility and any required bearer token. MCP supplies tools, not an AI model; OAuth and stdio are unsupported.", "Open AI Connections and select MCP server in the AI service menu. Selecting a saved connection stays on Choose service. Setup guide opens the official MCP 2025-11-25 Streamable HTTP specification; choose Configure connection when ready. In Configure, enter a name, full HTTPS endpoint (or HTTP on loopback) and optional bearer token.", "Continue to Validate and save. Connect and discover tools initializes the server and lists tools without calling one. Read any authentication, endpoint or protocol error, then Save connection to store settings and protect the token.", "Select the saved server in the main toolbar and choose Open MCP tools. Opening loads settings without contacting the server; Connect and discover tools explicitly lists tools. In Review tools, read a discovered tool description and schema as untrusted information. Enter the exact JSON object arguments, review their possible external effects, check consent and Run reviewed tool.", "Results are untrusted JSON and never applied to notes automatically. Cancel cannot undo server effects. Reopen a saved connection later; Manage saved connections provides New connection and Delete saved connection."], CommandIds.AIConnections),
        new("Manage saved AI and tool connections", ["Open Edit → Settings → AI connections → Manage AI connections. All saved API, GitHub Copilot and MCP profiles share one saved-connections list.", "Choose Create connection and select a type in the AI service menu. Enter only the fields required by that type, then configure, explicitly validate and save. Opening or saving sends nothing.", "Selecting any saved connection stays on Choose service and updates the AI service menu. Choose Configure connection to modify its settings, then validate again and save. Setup guide opens only official service documentation, including GitHub Copilot authentication and the MCP 2025-11-25 Streamable HTTP specification. To remove it, choose Delete connection and review the confirmation; other profiles and notes remain.", "Return to the main toolbar and select the saved profile. API and Copilot offer Write with AI. The writing panel shows the destination and exact request before generation and keeps the result as an undoable unsaved edit.", "MCP offers Open MCP tools instead. Connect and discover tools, choose Review tools, inspect the tool and exact JSON arguments, then consent to Run reviewed tool. Opening tools never sends the note; external tool effects cannot be undone by Tern."], CommandIds.AIConnections),
    ];

    public static IReadOnlyList<Tutorial> TutorialsFor(CommandRegistry registry)
    {
        string Shortcut(string id) => Gesture.Describe(registry.GetGesture(id)) is { Length: > 0 } gesture ? $" Current shortcut: {gesture}." : " This command currently has no keyboard shortcut; use its menu or Help → All Commands.";
        return Tutorials.Select((tutorial, index) => new Tutorial(tutorial.Name, tutorial.Steps.Select((step, number) => (index, number) switch
        {
            (0, 1) => step + Shortcut(CommandIds.NewFile),
            (0, 3) => step + Shortcut(CommandIds.Save),
            (1, 1) => step + Shortcut(CommandIds.OpenFolder),
            (1, 2) => step + " Quick Open:" + Shortcut(CommandIds.QuickOpen) + " Find in Workspace:" + Shortcut(CommandIds.FindInWorkspace),
            (3, 0) => step + Shortcut(CommandIds.CommandPalette),
            (3, 2) => step + Shortcut(CommandIds.Shortcuts),
            _ => step,
        }).ToArray(), tutorial.LaunchCommandId)).ToArray();
    }
}
