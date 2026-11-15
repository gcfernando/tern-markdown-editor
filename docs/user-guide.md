# Tern user guide

Tern edits ordinary Markdown files on Windows with a native preview. Start with the [beginner lessons](BEGINNER_GUIDE.md) for a first note. The [README](../README.md) covers downloads; [privacy](privacy.md) explains network behavior.

## Interface and modes

The Library holds files, folders and workspaces. Tabs select the active note. The canvas shows Source (`Ctrl+1`), Split (`Ctrl+2`) or Reading (`Ctrl+3`). The optional inspector presents outline, backlinks, broken links and statistics. A read-only reference pane helps compare another document without replacing the editor.

![Tern's Library, source editor, reading preview and outline in Split mode](images/gui/split-midnight.png)

Use File, Edit, Format, View and Help to discover actions. **+ Add** opens a form with a destination and name for physical files or folders. **New document** creates an unsaved draft. Quick Open (`Ctrl+K`) finds a note; Command Palette (`Ctrl+Shift+P`) finds an action. View → Reset layout restores panes without changing notes, fonts or shortcuts.

Use **Tab** and **Shift+Tab** to move through controls in either direction. Open dialogs and overlays keep keyboard focus within their own controls. Use **F6** or **Shift+F6** to move between visible areas in the main window, including the Library, document tabs, Source, Reading, and the selected Inspector tab. To resize a pane, tab to its divider and press the arrow keys. In Markdown Source, Tab can indent text.

## Open and edit local files

Use `Ctrl+O`, `Ctrl+Shift+O` or **+ Add**. The editor preserves detected encoding, BOM and LF/CRLF/CR line endings; new notes default to CRLF. Opening a file is not a request to normalize its text. Source editing includes syntax highlighting, undo/redo, formatting and find/replace. Broken-link repair is an undoable source edit.

Per-document positions and compatible session records can restore context. Restoration cannot restore a file removed by another program. The eager-load ceiling is 64 MiB; a file below it can still be expensive to parse or render. Preview has a separate smaller budget under [known limits](#known-limits).

## Saving, external changes and recovery

`Ctrl+S` saves, `Ctrl+Shift+S` chooses Save As and `Ctrl+Alt+S` saves all. Empty drafts can be saved. An explicitly chosen draft filename takes precedence over a heading-derived suggestion.

Saving uses staged/atomic replacement and detects competing disk changes. Tern reports failures and preserves detected displaced competing versions; this is not universal filesystem compare-and-swap across every filesystem or third-party editor. Inspect the external-change comparison or three-way merge before overwriting either version. Clean-document auto-reload is configurable.

Encoding-protected content cannot silently overwrite its original bytes. A separate UTF-8 copy is possible after confirming that displayed text may contain replacement characters. Inspect that copy and retain the original when exact recovery matters.

Local recovery checkpoints are scheduled after an idle interval. On startup, inspect provenance and compare recovered text, restore into an independent unsaved copy, save separately, discard or defer. Corrupt records are reported and preserved. Saved-text history is another local aid. Neither feature replaces independent backups of notes and attachments.

### Save As and portable attachments

Save As can copy referenced local files and linked Markdown dependencies **inside the original note's folder**, preserving their current bytes. Limits are **512 files**, **64 MiB per file** and **256 MiB total**. A conflicting attachment is retained and the note save is refused rather than replacing it silently.

Existing references outside that folder are not an unrestricted recursive copy request. Keep the note in its original folder or move required attachments inside first. Inspect the copied collection before removing its original. URL-import bundles have different limits.

## Pictures and local resources

Save a new draft before inserting a picture; Tern can open Save and continue insertion afterwards. Insert Local Image and Paste Image as Attachment use a form with a thumbnail, editable description (alt text), filename and destination. **Copy into note assets (portable)** suggests an assets folder. Uncheck it to link the original when a source file is known. Moving the original can break a link. Existing files are never overwritten. Undo removes the Markdown link but may leave a copied attachment on disk.

| Choice | Effect |
| --- | --- |
| Link original | Insert a path to the original; moving it can break the image |
| Copy into note assets (portable) | Choose a destination and link the saved copy; existing files remain protected |
| Cancel | Leave the note unchanged by that insertion |

Insert Local Image first checks for copied image files; otherwise it opens a picker. Paste Image as Attachment uses the same location rules for copied files. Clipboard pixels without a source filename need a destination. Source-area drops and clipboard payloads are distinct OS interactions requiring native acceptance.

Explicitly selected external image files can retain preview permission **per note and exact file** after reopening. This does not authorize every file in their folders. Other resources remain constrained by document/workspace context and resource policy.

Public remote image URLs can be requested automatically during preview or resource preparation. The setting controlling clicked external links does not disable those requests. See [remote images](privacy.md#remote-images).

## Open from URL

Use File or **+ Add** → Open from URL. **Open working copy** uses a complete managed cache when available; **Fetch latest** requests a new copy; **Save portable copy…** chooses where to save the file and assets. Imported Markdown opens as an editable unsaved copy. Editing or saving it does not upload changes back to the website.

The [current GUI task guide](gui-tasks.md#open-from-url) shows the current URL choices and their effects.

The loader accepts HTTP/HTTPS Markdown-file URLs, converts public GitHub file links to raw content and permits explicitly entered localhost URLs. Repository directories, issue pages, sign-in pages and arbitrary HTML pages are not Markdown documents. Public requests are constrained against private-network destinations, credential-bearing URLs and unsafe redirects.

The document limit is **10,000,000 bytes**. Directly referenced supported assets can form a bundle of up to **128 assets** and **100,000,000 bytes total**; this is not a recursive website crawler. Supported extensions include Markdown, common raster images, bounded SVG, PDF, text, CSV, JSON, YAML, ZIP and Office files. Download support does not mean those attachments are executed or rendered.

Complete cached bundles reopen offline. Partial downloads identify missing assets and can retry on reopening. Online Recent entries represent deliberate reloads, not ongoing synchronization. Verify the bundle before depending on offline access or sharing a portable copy.

## Folders, workspaces and search

Open Folder detects an existing named or legacy workspace; an ordinary folder opens for browsing without creating metadata. Workspace search uses an eligible indexed workspace. Create Workspace makes a new named directory containing `.tern-workspace.json`. Capability labels distinguish these contexts.

Independent roots stack in the Library, while `Company/HR` remains nested. Creation from a context menu targets that directory; the same form lets you inspect or change the destination. Use disposable folders to learn rename, move, duplicate and Move file to Recycle Bin: those actions operate on disk.

The scanner includes `.md`, `.markdown`, `.mdown` and `.mkd` by default, with optional text inclusion. Standard exclusions include `.git`, `node_modules`, `.obsidian`, `bin` and `obj`. `.ternignore` and settings exclusions narrow scanning. Directory links are not followed by default; permitted traversal remains constrained to the workspace. Default scanning skips individual files above **16 MiB**, independently of the editor's open ceiling.

Workspace search uses a local SQLite full-text index. Active-note find/replace and workspace search have different scopes. Indexes can be rebuilt; they are not document backups. Outline, backlinks and broken links depend on recognized syntax and available workspace files.

Press `Ctrl+F` to find text in the current note. All matches are highlighted in Source, Split and Reading, with a stronger outline for the active result. Search starts near your current position and automatically brings that result into view. `Enter`/`F3` moves forward; `Shift+Enter`/`Shift+F3` moves backward. The counter shows the current result and announces wrapping. **Results** shows clickable matching-line snippets. Use `Aa`, `W` and `.*` for case, whole-word and regular-expression matching; compact windows put these options under **⋯**. Invalid regexes and no matches have explicit messages. `Escape` closes Find and returns focus to the document.

`Ctrl+H` or the **Replace** toggle opens replacement controls. Replace all is one undoable change and does not save the file. Incomplete or timed-out results cannot be replaced in bulk. Find searches the Markdown source, so destinations and syntax hidden in Reading can also match: **Source-only match → Show in Source** reveals those results. Search does not combine words separated by Markdown formatting markers into a new plain-text phrase. Compact Results/Replace icons have tooltips and accessible names.

In **Inspector → Outline**, use **Search headings** and select a result to jump to that section. The H1–H6 labels indicate heading depth. In **Inspector → Links**, search by label, path, or domain. Each result shows the destination and link status. Select it to jump to the source line.

In **Inspector → History**, choose **Open version history** to compare a dated version with your current draft. If the list is empty, edit and save the file in Tern, then return. **Open as copy** creates a separate unsaved tab and keeps your original file and draft available.

## Close without deleting

| Action | Result |
| --- | --- |
| Tab × or `Ctrl+W` | Close the document after its save decision |
| Close & forget reference | Close associated tabs after save decisions, then remove the reference |
| Close Workspace, `Ctrl+Shift+W` or workspace × | Close its documents and release its context; unrelated notes remain open |
| Expanded folder × | Close its documents and collapse it; retain the folder entry |
| Close All Documents, `Ctrl+Alt+W` | Close tabs while retaining the workspace |
| Forget recent entry | Remove the reference while leaving open tabs alone |

Save/Discard/Cancel decisions complete before a group closes. Cancel preserves affected tabs and references. Closing does not delete files; separate file-management actions do. Window close also asks about unsaved changes.

## Export and print

HTML, PDF and text exports share document/resource readiness checks. A format change requires a new compatible destination. Read truncation, resource and unsupported-character warnings and inspect output before sharing. Export does not replace saving editable Markdown.

Choose **File → Export as… (`Ctrl+Shift+E`)**, select HTML, PDF or plain text, choose **Browse…** for the destination, then **Export**. PDF paper size, margins and page numbers and HTML appearance/language are in the same window. Document properties are optional and excluded by default.

- **HTML:** self-contained output embeds supported resources within limits and sanitizes unsupported active content. It is not a screenshot of the native view.
- **PDF:** bundled PDFsharp generates pages locally, with font/Unicode and layout constraints. Preparing remote images can still require network access.
- **Text:** produces readable text without the full styled Markdown layout.
- **Print preview… (`Ctrl+P`):** shows the formatted document in Tern. Select **All**, **Current page** or **Pages**, then **Open selected PDF…**. Press `Ctrl+P` in your PDF reader to choose a printer and copies. Opening the preview does not send a print job.

Resources must remain accessible under their local/remote policies. The PDF path bounds page count and image resources. A successful export call does not prove that every intended glyph, diagram and attachment is present; inspect the final artifact.

In **Help → Ask & learn**, choose an example or ask about using Tern. Questions without a matching installed guide topic are stopped locally. The assistant receives a Tern-only help instruction and installed guide facts. AI connection settings, conversation controls and sharing details are collapsed sections. Your notes, selected text and file paths are never attached; a bounded recent conversation is included for follow-up questions. General knowledge and content generation belong in a separate AI application.

## Settings and local data

For large notes, choose **Source** while editing to defer preview work, then return to **Split** or **Reading**. Workspace links update shortly after typing stops. Large searches and searches involving unsaved text run in the background; typing a new query cancels the obsolete request. If a preview reports truncation, the saved Markdown remains complete. Large merges can show broader conflicts to keep their memory use bounded.

Online imports fetch at most four dependencies at once and stop after a two-minute overall deadline. Assets stay in the local cache for offline use. Superseded snapshots older than seven days are cleaned at startup, while current snapshots and open tabs are protected. The **Feature Guide and Tutorials** includes **Large notes and background updates** with these instructions.

Settings (`Ctrl+,`) controls System/Midnight/Paper appearance, fonts, reading width, wrapping, line numbers, indentation, layout, session restoration, shortcuts and exclusions. New documents default to Extended Markdown and Split view. Shortcut preferences can override the defaults below.

| Data | Default location |
| --- | --- |
| Settings and connection metadata | `%APPDATA%\Tern` |
| Local state | `%LOCALAPPDATA%\Tern` |
| Recovery | `%LOCALAPPDATA%\Tern\recovery` |
| Search indexes | `%LOCALAPPDATA%\Tern\index` |
| Saved connection secrets | Windows Credential Manager, separate from those folders |

`TERN_DATA_ROOT` selects a root containing `settings`, `state`, `recovery` and `index`. Backing up it does not back up Windows Credential Manager credentials. Apply persists settings; Cancel discards unapplied values. Malformed settings can be preserved as `.corrupt` while defaults are used and the problem is reported.

Settings changes are pending until **Apply**. **Done** applies and closes; **Cancel** or Escape discards unapplied changes. AI connection and credential management has its own explicit save actions.

AI is optional. Read [protocols, credentials and reviewed text](privacy.md#optional-ai-connections) before using sensitive content.

### Set up an AI connection

Open **Edit → Settings → AI connections → Manage AI connections**. The wizard keeps its navigation and main actions together at the bottom.

1. **Choose service:** use the **AI service** menu for OpenAI API, Anthropic API (Claude), Ollama (local), Other compatible API, **GitHub Copilot** or **MCP server**. The choice enables the appropriate setup and fields. **Setup guide ↗** opens official documentation for the selected service, including GitHub Copilot authentication and the MCP 2025-11-25 Streamable HTTP specification. **Create connection** starts a new profile; the shared saved-connections list lets you select an existing one to modify or delete.
2. **Configure connection:** enter a recognizable connection name, the exact model ID and any required API key. Use **Advanced** for a custom documented endpoint or protocol. **Back** retains entered values.
3. **Validate and save:** review the destination, then choose **Validate connection**. This sends the fixed message `Reply with exactly: OK.` using the selected endpoint, model and key. No document is attached; provider usage or charges may apply. Read the success or error before choosing **Save connection**.

Saving stores metadata locally and secrets in Windows Credential Manager. Saving, reopening and navigating send no request; saving an untested connection does not prove that it works. Selecting any saved connection keeps you on Choose service and selects its service type. Choose **Configure connection** to open its saved settings, then modify, validate and save changes. **Delete connection** asks for confirmation before removing its saved settings and credential. **Create connection** starts a fresh setup. The compact **AI** entry opens the saved connection selector. Profiles identify Text generation or Tools capabilities; Help has a separate text-generation choice.

If validation fails, check the key and API permissions for authentication errors, the exact model ID and account access for unavailable-model errors, and the endpoint, TLS certificate and server/network for connection errors. Correct the setting with **Back** and validate again. A successful test describes that request; it does not guarantee future availability.

For a separate setup check, expand **Try your own message (optional)**, review the exact text and destination, check consent and choose **Send request**. For note editing, choose a Text generation connection in **AI** and use **Write with AI**. Review **Selected text**, **Entire note** or **Instruction only** before **Generate for review**. **Improve selected text**, **Summarize**, **Explain**, **Extract tasks** and **Proofread** fill an editable instruction. The sharing scope and destination are shown before generation. Compare original and proposed Markdown; **Apply changes** replaces text and **Insert result** inserts new content. Both are unsaved, undoable changes. **Copy result** copies the full suggestion; Close or Escape leaves the note unchanged.

Double-click empty space on the tab bar to open a blank **New document**. The AI scope picker stays visible even without a selection. **Instruction only** shares no note text and inserts a result. The review’s **Back** action keeps your instruction for revision.

Use the **AI** menu in Source for **Summarize**, **Extract action items**, or **Improve headings**. Summaries, explanations and checklists use **Insert result** below the source; **Apply changes** is reserved for replacement proposals. A newer edit blocks stale proposals.

Use **Ask workspace** from the AI entry to choose an eligible workspace, **Find passages**, and review the exact request. Uncheck any passage to exclude it; no request can be sent with all passages excluded. **Send reviewed request** uses only included passages. Citations open only verified files still in that workspace.

### Save GitHub Copilot or MCP settings

**GitHub Copilot:** have Copilot access and native `copilot.exe` installed. In GitHub, create a fine-grained token owned by your personal account with the **Copilot Requests** account permission; **Copilot Chat** is unnecessary. In the Copilot wizard, choose or name a connection, enter the token, leave Model at `auto` for a first test, and check the native executable in Advanced. Continue to **Validate and save**. **Validate connection** sends the fixed test message through the CLI; **Save connection** stores metadata and protects the token in Windows Credential Manager. The saved Copilot connection is also available for **Write with AI** and guide questions from the main toolbar. Writing sends a fixed validation test before the chosen text and instruction, and may reuse that successful check for up to 30 seconds with the exact same connection and credentials. Provider usage may apply to both requests. A changed note or connection during validation blocks generation. The complete Copilot request is limited to 8,192 characters, including the note and instruction; @mentions and CLI commands are not accepted. For a separate question in **Try connection**, paste only your question, review it, check consent and choose **Send reviewed text**. This separate question does not attach the note.

**MCP servers:** obtain a full Streamable HTTP endpoint with MCP 2025-11-25 compatibility and any required bearer token from the server owner. Configure a name, HTTPS address (HTTP allowed on loopback) and optional token. In **Validate and save**, **Connect and discover tools** initializes the server and lists tools without running one. Save the connection, then select it in the main toolbar and choose **Open MCP tools**. The workspace loads the selected saved server without contacting it. Choose **Connect and discover tools**, then use **Review tools** to read a tool description/schema and enter exact JSON object arguments. Review possible external effects before consent and **Run reviewed tool**. Results are not applied to notes. When MCP is selected in the writing panel, note-generation controls are disabled and **Open MCP tools** is offered instead. OAuth and stdio are unsupported.

Both integration wizards offer a saved-connections picker and **Manage saved connections** with **New connection** and **Delete saved connection**. Reopening saved settings makes no request. Changing a saved destination clears a restored token for reentry. Cancel stops waiting; it cannot recall transmitted text or undo a server tool's effects.

## Keyboard reference

| Shortcut | Default action |
| --- | --- |
| `Ctrl+N` / `Ctrl+O` / `Ctrl+Shift+O` | New / open files / open folder |
| `Ctrl+S` / `Ctrl+Shift+S` / `Ctrl+Alt+S` | Save / Save As / Save All |
| `Ctrl+W` / `Ctrl+Shift+W` / `Ctrl+Alt+W` | Close note / workspace / all documents |
| `Ctrl+Shift+T` | Reopen a closed document |
| `Ctrl+1` / `Ctrl+2` / `Ctrl+3` | Source / Split / Reading |
| `Ctrl+K` / `Ctrl+Shift+P` | Quick Open / Command Palette |
| `Ctrl+F` / `Ctrl+H` / `Ctrl+Shift+F` | Find / Replace / workspace search |
| `Ctrl+G` / `Ctrl+Shift+H` | Go to line / heading |
| `Ctrl+B` / `Ctrl+I` / `Ctrl+E` | Bold / italic / inline code |
| `Ctrl+Shift+K` / `Ctrl+Enter` | Insert link / toggle task |
| `Ctrl+Alt+V` / `Ctrl+P` | Paste Image as Attachment / print |
| `Ctrl+Alt+H` / `Ctrl+Alt+2` | Saved-text history / reference pane |
| `Ctrl+Shift+B` / `Ctrl+Alt+B` / `F8` | Library / inspector / focus mode |
| `Ctrl+,` / `Ctrl+/` / `F1` | Settings / shortcuts / guide |
| `Alt+Space` | Optional AI writing request in a supported document context |

## Known limits

- Eager loading is bounded at 64 MiB. **1 GB editing is unsupported**, and maximum-size responsiveness is not guaranteed.
- Preview budgets include 4,000,000 source characters, 250,000 nodes, depth 48 and a cooperative build-time limit. The underlying Markdown parse is not interruptible at every step. Preview/export can truncate before the file ceiling; heed warnings.
- Math and Mermaid are bounded native renderers, not complete browser LaTeX/Mermaid engines. Supported diagram families include flowcharts, sequence, entity relationship, state, class, mind map, pie and Gantt; unsupported syntax produces a notice.
- Local, embedded and remote images have distinct byte, dimension, path and format bounds. Scripts and active HTML/SVG content are not executed as browser content.
- Live AI, Copilot CLI and MCP compatibility needs separate verification; local fixtures do not certify every provider, model or entitlement.
- Physical clipboard/drop, printers, DPI, assistive technology, installed offline operation, performance and signing require native acceptance. See the [release ledger](V1_RELEASE_LEDGER.md).

Return to the [documentation index](../documentation.md).


### Version 1.0.4 workflows

- **Find:** use Edit → Find to highlight matches in Source, Split or Reading. Move with Next/Previous or select a result to reveal it. Review replacements before applying them.
- **Export:** choose File → Export as… (Ctrl+Shift+E), select HTML, PDF or plain text, choose a destination and export. Open file and Show in folder appear beside the completion status only for the current complete successful export; editing settings or changing format hides them. HTML embeds permitted images; PDF preserves document structure on printable pages. Plain text keeps readable text, lists, code indentation and table layout; it cannot contain fonts, colours or images.
- **Print:** choose File → Print preview… (Ctrl+P). Navigate actual PDF pages with the page number or previous/next buttons. Choose All, Current page or Pages, then Open selected PDF. The reader receives only the selected pages; Ctrl+P there chooses the printer and copies.
- **Ask & learn:** ask naturally, for example “I need to add a new AI connection, how can I do that?”. Tern ranks relevant installed guide topics instead of requiring every question word to match. Without a connection, or when a provider fails, an explicitly labelled offline answer appears automatically. No notes or selections are attached. Account access, expired tokens and provider permissions still require the account owner; Tern never changes them automatically.
- **Consistent controls:** dialogs share Paper/Midnight colours, inherited text scaling, wrapping fields, keyboard focus and visible primary actions. Compact screens scroll the form while preserving its action area.

### Help, export and guidance improvements

- **Ask & learn:** AI connection and conversation actions sit above the larger question box. Answers render Markdown headings, emphasis, lists, code and tables in one reading area, with the newest answer revealed first. Copy conversation and Start new conversation become available when an answer is visible. What is shared? explains the submitted data. Without a writing connection, Tern answers from installed guides; provider failures also recover to an explicitly labelled offline answer.
- **Export state:** changing Choose a format clears the destination, resets format options and clears completion status and notes. Open file and Show in folder appear only for the current complete successful result. Editing the destination or options, starting another export, or an export failure hides them. Actions share an aligned footer; Export notes start collapsed and only appear when needed.
- **Readable TXT:** paragraphs wrap to a preferred 80-character width. Compact tables stay aligned; wide tables become labelled records. Numbered link references keep long URLs out of prose and retain relative and section destinations. Code indentation, nested lists and Unicode remain intact. Text files cannot display fonts, colours or actual images.
- **PDF badges:** supported inert SVG images use the same bounded native renderer as preview and are embedded as PNG images. Unsupported SVG features still produce an export note instead of silently dropping content. HTML retains permitted SVG images.
- **Next step guidance:** the main window and shared dialog shell show a contextual tip and installed Learn more help. Export tips change for destination selection, progress, success and failure. Ask & learn has its own question and answer guidance.

### Latest screen usability improvements

Ask & learn gives the answer more space once it is ready; the question editor stays available in a compact form for follow-ups. Click a picker result once to choose it, or use Up/Down and Enter. The welcome panel is larger and centered.

In Write with AI, highlight text before opening the panel to use the selection actions. Review the sharing scope and connection, write your instruction, then choose Generate for review. The raw request/JSON section has been removed.

In Review AI suggestion, compare the complete BEFORE and AFTER text. Use Back to improve the instruction, Copy result to copy the full suggestion, Apply changes to accept the proposal, or Close to keep your note unchanged. Insert result preserves the original text and adds the generated content. Applied edits remain unsaved and can be undone. Narrow screens stack the comparison vertically. Disclosure sections use the same themed appearance and keyboard focus cues throughout the app.

## Version 1.0.5 workflows

See the [illustrated 1.0.5 workflows](workflows-1.0.5.md) for onboarding, ten starter templates, tags and metadata, link destinations, image insertion, line endings, print page selection, Windows distribution and Explorer, and offline Ask & learn. The same instructions are available in the installed Feature Guide and Tutorials through Help or F1.
