using Tern.App.Commands;

namespace Tern.App.Help;

public static partial class FeatureCatalog
{
    // A property, not a field: static initializer order across partial files is undefined.
    private static Tutorial[] Release106Tutorials =>
    [
        new("Rewrite or summarize selected text with AI", ["Select text in Source, then press Ctrl+Shift+Space or choose Edit → Write with AI.", "Text to share shows Selected text. Choose Instruction only if the AI should not receive your text. To share the entire note, close the panel and clear the selection first.", "Choose a starter such as Summarize or Proofread, edit the instruction, then choose Generate for review. A progress indicator stays visible until the request finishes or fails, and Cancel request stops it.", "Compare BEFORE and AFTER. Apply changes replaces only your selection. Back returns to the instruction, and Close leaves the note unchanged.", "Undo restores the previous text. The change stays unsaved until you save the note."], CommandIds.AskAI),
        new("Connect DeepSeek or a custom AI service", ["Open Edit → Settings → AI connections and choose Manage AI connections.", "For DeepSeek, choose DeepSeek API, enter your API key and a model such as deepseek-flash or deepseek-v4-pro. For another service, choose Custom AI Connection.", "A custom connection needs the full API endpoint URL, its protocol, the exact model ID and an API key if the service requires one.", "Choose Continue to validation, then Validate connection. Tern sends one short test message and shows the connectivity, authentication, model and generation results. Your notes are not sent.", "Choose Save connection. The key is stored in Windows Credential Manager, and the connection appears in the AI menu."], CommandIds.AIConnections),
        new("Start an AI project with instruction templates", ["Choose New from Template… and search for AGENTS, CLAUDE, GEMINI, SKILL, ARCHITECTURE, CONTEXT or REQUIREMENTS.", "Choose Use template. The new draft keeps the exact file name that tools expect.", "Replace each bracketed placeholder with your project's real commands, paths and rules. Delete sections that do not apply.", "Save AGENTS.md, CLAUDE.md or GEMINI.md at your project root. Save each skill as SKILL.md in its own folder, such as .claude/skills/code-review/SKILL.md."]),
        new("Choose a theme", ["Open View → Theme and choose Paper, Linen or Mist for a light theme, or Midnight, Graphite or Harbor for a dark theme. Follow System uses your Windows setting.", "The theme applies immediately to the editor, Reading view and dialogs without changing your documents.", "Edit → Settings → Theme and accessibility changes the same saved preference. Windows high-contrast colors take priority over any theme."]),
        new("Use Reading options", ["In Split or Reading view, choose Options in the Reading preview header.", "Sync Source and Reading scrolling keeps both panes together in Split view. Use full reading width fills the pane instead of a centered column.", "When the toolbar is narrow, Go to top, Page up and Page down appear here. Edit source returns to the Markdown."]),
    ];
}
