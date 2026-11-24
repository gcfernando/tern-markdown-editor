# Changelog

## 1.0.6

### Characters sent to AI

- Each AI connection saves its own **Characters to send at one time** value. New connections start at 256,000. Older connections without a saved value receive 256,000 once. Choose any whole number from 8,000 to 1,000,000. MCP tool servers do not have this field.
- Every AI screen uses the saved value, including Explore, writing, Help, Learn Tern and connection test messages. Help shares the global AI choice.
- Explore reads all ticked notes, in smaller parts when needed, up to 1,000,000 characters per question. It shows what AI read and names anything left unread. A simple one-fact question may start with the best sections, then read everything before saying it was not found.
- Version-range questions, such as “Explain all super features from version 1 to latest”, cover the full range.
- If AI refuses a size, Explore halves the request size, down to 8,000 characters, and suggests a lower value without changing the saved setting. Write with AI warns before sending oversized text and keeps the whole note unchanged.

### Fresh AI requests and complete reports

- Every AI step and retry starts fresh, including Help, writing, Explore, connection tests and report preparation. Email copied from a ready report is built locally without an AI request. Only the current request and its needed notes, facts or draft are supplied; earlier conversations and provider sessions are not resumed. Tern's instructions and current task data use the appropriate provider roles.
- The saved character limit counts all instruction and data text, including connection-test instructions, rather than user text alone.
- Reports start with an executive summary, followed by detailed findings, bold keywords, exact source evidence and a final review or decision record with room for signatures and a date. Quotations, source headings and lines are checked against the current notes. AI checks the complete report before it is ready; evidence still needs human review.
- A failed report check offers Ask AI to correct these issues. Only an explicit click requests a correction using the current report, issues and notes. Quotations, evidence and the full report are checked again; a new draft requires your review, and failures or changes keep your previous edits and issues. No output happens automatically.
- Report review uses short progress and plain recovery messages. Preview report opens the formatted report; detailed check issues stay under What needs checking. After an edit, Check report validates the changes without exporting.
- Large reports can recover from an unfinished reply through smaller independent source sections, retaining checked findings and evidence. These requests start fresh and the whole report is checked before Ready.
- Copy for email builds a professional executive summary locally from the checked report. Email, Markdown, PDF and printing reuse one validated result; switching formats does not reread notes or ask AI again. Saving a ready PDF opens the destination picker without a last-minute AI check. Report or source changes require renewed checks. Copying does not send email or attach a file.
- Explore restores unfinished work and report edits at the last stage without contacting AI. Normal workspaces use a protected local checkpoint; Private workspaces keep tasks in memory while Tern is open. Done finishes the task; copying or exporting does not. Interrupted work waits for Retry, and changed sources require Refresh notes before reuse.

### Messages and colors

- Tern now has four clear message levels: Information (i), Success (✓), Warning (!) and Error (×). Each has its own color and symbol, in every theme.
- Tern warns you before you press a button when something needs fixing, and a disabled button says why and how to fix it.
- A file or folder with a word like "error" or "review" in its name no longer changes a successful message into a warning or error.
- Many technical system errors are replaced by a plain reason and a next step. The technical wording stays in Activity.
- Focused dropdowns now follow the theme color in light themes.

### Explore your notes review

- Search now ranks notes by file name, headings and content, and common words count for little. Questions such as "What are the latest changes?" now find the changelog and read its newest version.
- Relevant notes are ticked automatically and always shown. You can untick them or use Add a note, and Tern keeps your choices until you start a new question.
- AI works at every step. It adds words with the same meaning, chooses the notes from their names and headings and says why, suggests what else to read, writes the answer, fills in the report, checks your final report wording and advises what to try when nothing is found. Email is built locally from the checked report. No note text is sent until you press Get my answer.
- AI interprets questions in everyday language; only explicit advanced search syntax is treated as filters. Empty results explain what the search found without claiming every note was read or that no note contains the answer. Unfinished reading, unreadable, left-out or unsupported files and AI failures have a next step.
- Ticked notes are read in parts when they do not fit one request, so one long note no longer crowds others out of a report.
- Report title, main question and initial findings start from your question and the checked answer, then expand into the complete report. Your wording stays editable. Release dates written in notes are kept separate from file saved dates.
- Explore your notes needs AI. In Offline mode it stops on the first screen, says why and offers Open Settings.
- The question box shows 10 lines when the window has room, and choices use a modern card design.
- Reports say when a long note was only partly read, and list any number Tern could not find in your notes.
- Before printing a report, Tern names the printer it will use.

### Reliability fixes

- Trim on save keeps Markdown line breaks made with trailing spaces before more text.
- One failed preview or document check no longer stops later ones.
- Move no longer moves a file when you double-click empty space in the folder list.
- Compare no longer shows the wrong file after the selection changes.
- Confirmation boxes without a default button start on the safe button.
- Insert Link warns before linking to a file on another drive, and Insert Image shows folder errors in the dialog.
- A damaged saved copy of an online file falls back to a fresh download.
- Background Git checks stop more safely, and the printer list is no longer read on every keystroke.

### AI reliability

- AI failures say plainly whether sign-in expired, there were too many requests, there is no internet or the service has a problem.
- A request to wait from an AI service is no longer reported as a broken connection or as too much text. Canceling stays a cancel, and a timeout is explained as a timeout.
- GitHub Copilot accepts notes containing @ names and email addresses.
- Claude connections can write longer replies so full reports fit. Older models use their own smaller reply allowance when needed.

### Learn Tern corrections

- Lessons, Help and guides were checked against the app and corrected, including printing, Open from URL, AI steps, shortcuts, themes, version history and image replacement.
- A new Help topic, What Tern's messages mean, explains the four message levels.
- The sources used are listed in [Learning content sources](docs/learning-sources.md).

### Removed

- The unused Add Destination dialog, replaced earlier by Create item, and unused internal code.

### Opening folders and workspaces

- Large folders and workspaces show their file tree almost immediately. Links and search are prepared in the background while you keep working, and you can open or close another workspace during indexing.
- Indexing is much faster, and progress updates no longer overload the window on large folders.

### Guidance and errors

- Explore your notes is redesigned as three clear steps: Ask, Check your notes, Your answer, with 12 suggestions, AI help for unclear questions and a clear not-found screen.
- PDF, note and print now create a reviewed A4 decision report from the ticked notes. Follow-up, technical details and the separate Summarize, Action items and Compare buttons are removed.
- File names with underscores now display correctly.
- The redesigned flow uses the one global AI selection and opens AI Connections when no AI exists.
- Check your notes adds Select all and Select none. Get my answer stays disabled until at least one note is ticked.
- Search skips license and dependency files, removes duplicates and matches word forms such as decide and decided.
- Share this answer can copy a clean email message, save a PDF, add the answer to a note and print to the default printer, in any combination.
- GitHub Copilot answers are faster, and first-time indexing of large workspaces is faster.
- All starter templates are rewritten from their official formats, including AGENTS.md, CLAUDE.md, GEMINI.md, Agent Skills, MADR, Keep a Changelog, arc42, EARS and the Google SRE postmortem.
- Learn Tern now opens on Tern University, with five courses and new lessons for templates, search, Explore your notes and sharing.
- Every Explore your notes screen explains how to use it, gives a tip and says what you get at the end. The review screen shows where each passage came from and what is not shared.
- A plain folder can be made ready with one button, keeping your question.
- GitHub Copilot replies keep their exact text instead of being wrapped to a terminal width.
- Explore your notes sends the passage that actually answers the question, not only a matching title.
- Errors for opening folders, Explore your notes and unexpected problems now say what happened, why, and what to do next.

### AI writing

- With text selected, Write with AI offers only Selected text or Instruction only. Reviewed results replace that selection. Whole-note summaries, explanations and task lists are inserted so they cannot overwrite the note.
- Generation shows a progress indicator, a Generating state and a clear failure message. The review screen is larger and easier to compare.
- Processing indicators stay visible when Windows animation effects are off. With Tern's Reduce motion setting on, they show a still elapsed-time counter instead of movement.
- An account with no remaining credit now gets a clear billing message from API connections and Copilot.
- Added a DeepSeek API connection. Other compatible API is now Custom AI Connection, with clearer setup details and connection validation.

### Interface

- Visible scrollbars keep clear space from text boxes and cards.
- Added Graphite, Harbor, Linen and Mist themes.
- Reorganized the Reading Options menu.
- Print preview no longer opens with an extra border around All, and the title-bar Minimize icon is corrected.

### Templates and Help

- Added AGENTS, CLAUDE, GEMINI, three SKILL (code review, debugging, testing), ARCHITECTURE, CONTEXT and REQUIREMENTS templates. Drafts keep the exact file name each tool expects.
- Added tutorials for selected-text AI, DeepSeek and custom connections, AI project templates, themes and Reading options.

### Diagrams and PDF export

- Long connectors use a straight line, a single bend or one clear lane, removing unnecessary jogs. Connections sharing a node face no longer cross because of port order, and labels avoid the corners of their lines.
- Group titles and sequence labels are drawn clear of connectors and lifelines, and PDF connector joins are rounded.

### Expanded Settings preferences

- Added separate Source space, tab and line-ending markers; a configurable column ruler; line spacing; scrolling below the last line; and whether Copy/Cut uses the current line when nothing is selected. These options apply consistently to Source and read-only Reference without rewriting notes.
- Reading now has its own font-family preference. In-note Find shares saved case, whole-word and regular-expression choices with Settings, and Library Tasks shares its saved grouping choice. Search patterns and replacement text are not stored as preferences.
- Settings now edits the actual PDF and HTML export defaults, with selected-format reset and staged forgetting of remembered export folders. Plain text exposes no ineffective page or theme controls. All 61 preferences use the same 11-category Settings navigation and Apply/Done/Cancel workflow.

### Settings navigation

- Settings uses a deliberate ordered set of descriptive destinations shared by the sidebar and compact picker. Reading and views groups preview typography, reading width, synchronized scrolling and startup view; source typography lives with Writing and editing, and external-link permission lives with Privacy and network.
- Existing category entry points, preference IDs, pending Apply/Done/Cancel behavior and search remain compatible. Installed help uses the visible category names, and reset stays limited to the selected category.

### New-file preferences

- Settings → Files now saves default encoding, UTF-8 BOM and CRLF/LF/CR line endings for future blank documents, templates, daily notes and physical file creation. UTF-16/32 new files include a BOM for reliable reopening; legacy encodings retain strict lossless-write checks.
- New defaults leave existing/opened documents unchanged. Restored discarded drafts retain their original exact text and format. A newly created file's encoding hint applies only when the same loaded bytes match its creation receipt; changed files use ordinary detection. Unrepresentable new content leaves no empty destination.
- Theme remains the shared saved appearance preference, and default-view help now describes its actual startup/workspace-layout scope.

### Menu organization

- Grouped less frequent file operations under Folders and workspaces, Document actions, History and recovery, and Encoding and line endings while keeping common new/open/save/export/print/close commands directly available.
- Separated Edit search and AI actions, grouped heading and table operations, and separated View layout, appearance, zoom and accessibility commands. Library views now names sidebar navigation separately from the Library visibility toggle. Installed help and user-guide menu paths follow the new groups; command IDs and shortcuts are preserved.

### Shared motion and operation feedback

- Library/Inspector, shared dialogs/overlays, notifications, controls and tab highlights use centralized optional motion. Panel collapse, modal blocking, focus changes and safe close remain immediate; editor text never fades.
- Reduce motion combines the app setting with a read-only Windows animation-preference query. Reduced motion retains phase text and measured progress without an indeterminate animation.
- Activity follows actual save, index, export, print, AI, merge, Library, repair, recovery, search, URL and image workflows. Human decisions pause indicators; reliable counts are measured, and unsupported cancellation has no fake Cancel action.
- Shared loading waits 200 ms of actual running work, including after a human decision, and stops indeterminate animation on terminal outcomes. Progress updates retain existing controls and focus.
- Print progress counts pages handed to Windows, not physical output. AI suggestions remain unapplied until approved; restored/history/image/repair buffer edits remain unsaved until explicitly saved. Partial results and post-save warnings stay distinct from full success.

### Advanced editing and persistence (WP31)

- Export remembers successful per-format options and per-workspace output folders, without bypassing the destination picker.
- Source can optionally show safe local image thumbnails, undoable task checkboxes, scaled headings and dimmed markers away from the caret line. Original Markdown remains editable; Source thumbnails never fetch remote images.
- Outline folds headings and offers single-Undo sibling-section movement and whole-subtree promotion/demotion. Status shows selected words; Details shows session net words and optional `target-words`.
- Optional real-file autosave defaults off and uses the normal save/conflict path after 30–3,600 idle seconds. Windows contrast preferences override the chosen theme using system text, background, border and focus colours.
- New History and recovery copies use Windows CurrentUser DPAPI. Workspace Private mode disables History/recovery/discarded copies and uses memory-only search; confirmed file/workspace History clearing preserves source files.
- Don't save retains a protected seven-day safety copy under File → Open Recent → Recently Discarded Drafts, except for Private notes. Reopening creates an unsaved copy.
- Read-only Git markers distinguish modified/untracked files. Compare with Last Git Commit shows HEAD against a Source snapshot, including unsaved edits, without staging or committing.

### Knowledge navigation (WP30)

- Typing `[[` in a saved workspace note offers resolving document, alias and heading links. Insert Link includes Workspace note; insertion is undoable and rejects changed or protected targets.
- Library Tags shows document counts and starts tag searches. Save search pins a persisted, workspace-scoped query in Library Shortcuts.
- Workspace Tasks has undoable checkboxes, file grouping and path/tag filters. Closed task notes open in the background; edits remain unsaved until Save.
- Heading edits offer reviewed incoming-anchor updates. Replace in Workspace previews exact changes, includes unsaved buffers and refuses incomplete or stale results. Closed-file changes retain History and a guarded Undo action; divergent duplicate buffers are not overwritten.
- Inspector Links exposes unlinked title/alias mentions and an optional one-hop adjacency view of incoming/outgoing neighbours.

### Writing productivity (WP29)

- Added guarded, one-undo table operations, cell navigation, quoted CSV/TSV table paste, and list indent/outdent across menus, palette and Source.
- Normal paste converts bounded HTML headings, lists, links, emphasis, code and simple tables to Markdown. Active content is removed; remote images remain alternative text. Invalid fragments and unsupported merged cells report an error without changing the note.
- Ctrl+Shift+V now pastes plain text; Paste Image as Attachment defaults to Ctrl+Alt+Shift+F3. Explicit shortcut customizations remain registry-managed.
- Optional Windows-local spell checking underlines prose and offers undoable corrections, excluding code, destinations and front matter. Work is debounced, viewport-bounded and revision-guarded; dictionaries are not modified.

### Operational regression verification (WP28)

- Added the full clean/dirty, modified/deleted conflict-decision matrix, deterministic identity-queue/fault recovery, actual oversized watcher removal/reappearance and SQLite FULL/BUSY save/refresh faults.
- Opt-in diagnostics now record status severity as well as boundary exception types, without status/action text, exception messages or source-file paths. Explicit copied reports correctly advise reviewing sensitive context before sharing.
- Added both-theme referenced-resource resolution, tampered newest-history fallback, missing recovery-directory save and executed-result-to-audit-ID evidence checks.

### Safety cleanup and recoverability (WP27)

- Save-time whitespace normalization preserves code/front matter and only updates a successfully saved current buffer; failure, conflict and cancellation preserve text/revision.
- Unknown settings enum values default independently; versioned migration, transient-lock retry, last-good backups, unique corrupt copies and explicit startup recovery warnings protect other preferences and shortcuts.
- BOM-less UTF-16/NUL-bearing auto-detected files open read-only with encoding-reopen guidance. Late AI replies remain copyable; stale Apply is disabled and explicit cursor re-anchoring requires a fresh insertion review.
- Document rename plans run outside the identity gate on a worker; delete confirmation also releases it. Physical operations revalidate before mutation. Overlapping close requests explain the outstanding save prompt.
- Export has real cooperative cancellation through preview, renderers, image reading, PDF serialization and atomic temporary output. Cancel preserves existing targets and allows retry.
- Settings can remove only Tern's Explorer registration; title version comes from assembly metadata. Release preparation refreshes all maintained documentation screenshots with matching version captions and hash evidence.

### Workflow polish and discoverability (WP26)

- Empty named workspaces offer new-note, byte-preserving no-overwrite import and help actions. First saves retain a Show in folder action; newly created notes focus Source after opening and layout.
- Tags start scoped workspace searches; clickable search-syntax examples and Inspector backlinks keep navigation beside the document.
- Find tabs directly into Replace, with scoped Ctrl+Alt+Enter for Replace all and a disabled-state explanation for limited results.
- Export a copy opens its picker on the first Export click, keeps the folder/stem when formats change and invalidates obsolete completion actions.
- All guide entry points reuse a navigable Learn Tern window. Main guidance dismissal persists independently of dialog help; Settings restores it. Settings, Shortcuts and Learn Tern use a shared scaled compact breakpoint.
- Reading options use the shared popup theme and only duplicate navigation when the toolbar cannot show it. Tab contexts add path/reveal/reopen actions and middle-click uses the normal unsaved-close flow. Library menus keep file-scoped actions and destructive choices last.
- URL, AI and preview errors retain explicit recovery actions in Activity; failed preview has an inline placeholder. Restore summarizes documents with changed persisted disk baselines, excluding touch-only changes and unsaved-buffer differences.

### Screen-reader and icon accessibility (WP25)

- Added View commands Read Current Line (Ctrl+Alt+Shift+F1) and Read Document Status (Ctrl+Alt+Shift+F2), also available in the palette and Shortcuts. Position feedback uses a polite region; line reads are bounded and document status distinguishes the last analyzed word count from a pending count.
- Source automation still exposes the canonical editable Value, but full-document change events now wait for 500 ms of idle and are cancelled when the editor detaches. Source announcements are not copied into Activity.
- Replaced decorative glyph actions with shared vector icons, preserving theme foreground, maximize/restore state, tooltips and accessible names. Picker inputs name their current mode, connection wizard actions use explicit step names, and checklist states say done/not done.
- These changes do not add TextPattern/text-range support or certify native NVDA/Narrator behavior; native acceptance remains separate.

### Ask My Knowledge Base

- Added a separate, resizable nonmodal assistant window with local workspace search, bounded heading-aware excerpts and separate wiki-link/backlink/tag suggestions. Local retrieval works offline without an AI provider. The menu/palette command has a remappable Ctrl+Alt+F2 default.
- Model requests require a reviewed exact context and per-request consent; remote sharing is off by default. Existing credential, network, timeout and provider checks are reused.
- Conversations stay in memory, keep bounded follow-up context and clear on New conversation or Close. Source snapshots are revalidated before sending, accepting an answer or opening a citation; invented or inconsistent citation IDs are rejected.
- Summarizing, comparing, explaining, outlining, internal-link suggestions and action-item extraction use reviewed sources. Inserting a result opens the existing explicit approval flow, freshly revalidates sources at approval and preserves undo and document-revision protection. Closing review cancels pending validation; repeated approval cannot apply twice.
- Redesigned the cramped dock after user feedback: three guided steps, editable example questions, contextual tips, complete request disclosure, dedicated follow-up input and answer copying. Buttons, fonts, spacing, cards and colours reuse Tern's shared GUI standards; step fades respect Reduce Motion.
- Compact/scaled layouts scroll and stack review columns without reducing editor space. Opening sources or insertion reviews minimizes the assistant; reopening returns to the same conversation.

### Coherent themes and live typography (WP24)

- System appearance now switches both Tern and Fluent resources at runtime, with editor and preview redraws. Fluent checkboxes, radios and progress use each theme's teal accent.
- Print canvas, overlay scrims/shadows and shared source/preview find highlights use theme resources instead of fixed colours.
- Shared caption/body/lead/section/title, radius and spacing tokens replace parallel dialog sizing. Open dialog text updates when Interface text scale is applied; saved scales are clamped to keep captions at least 11 pixels.
- Paper warning and syntax colours meet normal-text contrast on chrome surfaces. Splitters retain subtle decoration but show contrasting hover/keyboard grips with wider hit targets.

### Command terminology and discoverability (WP23)

- Unified command names across menus, detached flyouts/context menus, palette, Welcome and command help, retaining access keys and stable IDs. Standardized Library, Inspector, Learn Tern and Edit tags terminology and added UI-label lint.
- Added Go, zoom, encoding, reload/reveal, recovery fragments, AI connections, task-at-caret and standard clipboard menu actions. Clipboard shortcuts honor registry overrides; Cut awaits a successful copy before deleting.
- Unavailable commands remain in the palette with actionable reasons and cannot execute.
- Theme switching follows the actual appearance; theme radios and panel/focus/reference checks expose current state.
- Open Recent lists recent files, workspaces and URL references without opening them automatically; More opens Recent and confirmed pin-preserving Clear Recent remains last.
- URL guidance names real controls, and Settings/profile/export choices and activity use readable enum display names.

### Dialog safety and editing transactions (WP22)

- Standardized dialog footers: secondary/destructive actions left, safe default primary followed by Cancel/Close right, with responsive wrapping and one accent default. Connection wizards update their primary by step; guidance uses explicit keys rather than dynamic-title matching.
- Reset and connection deletion/discard actions use danger styling and never default to destructive approval.
- Settings text fields no longer activate Done on Enter. Cancel/Escape/window close confirms unapplied edits, search disables category reset, and failed persistence preserves pending edits.
- Shortcuts has its own working copy with Apply/Done/Cancel from both entry points; Reset all confirms and stays pending until applied.
- Clear Recent History confirms its file/workspace/URL/closed-tab scope and keeps pinned references. Explicit Recent reset can still remove pins.
- Inline tab renames commit on focus loss or Enter, cancel on Escape, and keep invalid names editable with visible error/status feedback.

### Command feedback and templates (WP21)

- Unavailable commands explain their requirements in Info status and Activity, including workspace promotion, unsaved drafts, read-only notes, undo/redo and busy operations.
- New from Template… is available from File, + Add and the palette with Ctrl+Shift+I, preserving Ctrl+Shift+N for New File in Folder. Dated unsaved drafts expand date, time and title variables.
- User templates load from the Settings templates folder and workspace `.tern\templates`, with source labels and explicit loading-limit/error warnings.
- Open Today's Note creates or opens `Journal\YYYY-MM-DD.md` in the active workspace without replacing existing content or unsaved edits.

### Save workflows, session state and printing (WP20)

- Save As/copy can leave outside attachments uncopied with a path-list warning or rewrite their links to absolute local paths, including copied Markdown dependencies. Cancel remains the default; source files, overwrite checks and destination containment are preserved.
- F5 explicitly reloads from disk even when automatic reload is disabled; dirty text always requires the discard confirmation.
- Tabs, workspace/folder sections and selection are saved after a short debounce during use. Restore, exit and reset cannot overwrite the session with partial or stale state.
- Later launches forward file arguments to the running instance for the same user/data directory without restoring its session again. Settings use unique atomic temporary files.
- Print preview now offers Print… through the Windows printer dialog for the selected formatted PDF pages, with external PDF opening secondary. Owned temporary PDFs are removed on close, and startup removes generated previews older than one day; cleanup failures are reported.

### Large notes and workspace preparation (WP19)

- Workspace scanning, indexing inputs and disk text providers now share the editor’s 64 MiB default limit. Documents between 16 and 64 MiB remain available to the tree, Quick Open and link analysis; explicit smaller limits remain supported.
- Search extraction skips pattern matching on blank lines and includes rule/table delimiter matching in its existing timeout fallback, so a very long whitespace line cannot abort indexing.
- Recovery writes UTF-8 checkpoints with backward-compatible legacy reading and exact preservation of unpaired surrogates. A 60 MiB ASCII note fits; large-note idle delays scale to reduce repeated writes.
- Version history explains its per-document budget and age/count retention, including when a large document cannot fit one version. Oversized history warnings point to Settings without blocking the document save.
- The Library appears after scanning while graph/search preparation continues with progress and Cancel indexing. Cancellation keeps the file tree and explicitly marks content results incomplete; Rescan resumes preparation.
- Online-only files remain visible and searchable by name without automatic content reads. The Library marks them online-only; locally available cloud files are accepted by attachment guards while symlinks, junctions and unrecognized reparse points remain blocked. Native OneDrive validation is pending.
- Recovery also checkpoints during continuous editing, with a maximum scheduling interval of 10 seconds instead of indefinitely waiting for idle.

### Network privacy and connection controls (WP18)

- Remote images default to Ask, with Load once and Always for this file consent, plus Always/Never settings shared by preview, export and print. Approvals are scoped to document identity; help answers remain network-image-free.
- Added Offline in the status bar and Settings → Privacy to block remote images, URL import, AI, MCP and Copilot, with feedback in Activity.
- AI API timeouts are configurable per connection (1–3,600 seconds); defaults are 120 seconds remotely and 300 seconds locally. AI/MCP honor the system proxy; image/URL SSRF protections and direct connections remain.
- Replaced the interim Copilot command-line warning: reviewed prompt text travels on standard input, and regression fixtures assert it is absent from process arguments.

### Unreachable and moved files (WP17 / S-01, S-02)

- A file whose drive, share or device does not answer (sleep, VPN drop, unplugged share) is no longer reported as "moved or deleted". The document shows "Unavailable — will retry" (warning), keeps its baseline, Save explains the location is unavailable instead of writing, and Tern checks again when you return to the document; once it is back, Save works without a conflict dialog.
- When a file or one of its folders is renamed or moved outside Tern, an info bar offers "Follow" (the tab, history, Recent and recovery move to the new path, unsaved edits are kept) or "Save here anyway…".
- A Save that would have to recreate a vanished folder asks first (Recreate folder, Save As…, Cancel); it is never recreated silently.

### History, recovery and image replacement (WP15 / WP16: B-13, P-04, R-01, R-04, R-02, R-06, B-15)

- Version history and recovery review use a line-highlighting diff with change count and Previous/Next change; history can replace the open document (one undo step); recovered drafts show their original file's history and can replace the open original.
- Moving a document to the Recycle Bin keeps its history in a 30-day trash (restored with the file) and keeps dirty-tab recovery records. History is thinned by age (all for 1 hour, then hourly/daily/weekly) within a new "Version history size" setting (default 32 MiB).
- Replacing an image sends the old file to the Recycle Bin and lists referencing notes.
- New "Recovered Save Fragments…" command (Ctrl+Alt+Shift+F4) and post-open banner for leftover save backups/temp files.
- Link repairs capture each closed file's earlier text in history and offer "Undo link updates".

### Changed-on-disk surfacing and merge correctness (WP14 / D-02, B-10, S-07, B-08, B-29, B-34, B-23, B-31)

- A file changed outside Tern while you have unsaved edits no longer opens a modal prompt on focus, tab switch or file-watcher events. The tab shows a ⚠ "Changed on disk" badge (also on the Library row) and a bar above the document offers Compare…, Reload (confirms before discarding edits) and Keep mine (the disk version is kept in History). The conflict dialog appears only when you Save.
- Closing, restoring and removing tabs no longer trigger disk-change prompts; only user tab activation checks the disk. Disk checks wait for any save, move or rename of the same document and check only the file that changed.
- Compare… now leaves the merged text in the editor unsaved ("Merged — review and save"); only a merge started by Save also writes it. The merge window says which will happen.
- Conflict dialog wording distinguishes a clean tab ("no unsaved edits in Tern") from one with edits.
- Three-way merge: a disk change that only switches LF/CRLF is not a conflict and the editor's dominant line ending is kept; adding or removing the final newline is tracked; "Keep both" at end of file puts the two versions on separate lines.

### Move, duplicate, folder rename and folder recycle (WP13 / B-07, D-04, R-05)

- New Library actions and File → Move Document… / Duplicate Document: move a file or folder within its workspace (picker or drag onto a folder), duplicate as "name copy.ext", rename folders, and move folders to the Recycle Bin after a confirmation that counts files and unsaved documents. Nothing is ever overwritten.
- One identity-change routine now migrates open tabs, Recent entries, view states, saved history, recovery copies, closed-tab list, link graph and linked-image permissions for Rename, Move, folder operations and Save As; failed steps are reported as warnings. Link repair review follows every rename and move.
- Library file menu is reorganised: tab-only commands are gone from it and the Recycle Bin command is last and red.
- Saved-text history and linked-image permissions now belong to a specific file, not just a path. A different file created later at the same path no longer shows the old history or inherits the old image permissions.
- Unreachable history (hidden entries, and history of files that no longer exist) is removed after 90 days.

### Make Folder a Workspace and scalable document text (U-22 / G-01 / B-26 / P-16)

- New command File → Make Folder a Workspace… (also in the Library folder section and tree menus): after a confirmation it writes `.tern-workspace.json` into a browsed folder and opens it as an indexed named workspace, enabling content search, backlinks, broken links and tasks without moving files. Refused inside an existing workspace; the descriptor is rolled back on failure.
- Browsed (non-workspace) folders are no longer content-indexed; they keep the tree, filename Quick Open and link graph.
- New setting "Also scale editor and preview text" (off by default) applies the UI scale to editor, reference and preview text.

### AltGr-safe shortcuts, window chrome and small/high-DPI fit (WP10 / WP11)

- No default shortcut is Ctrl+Alt+letter/digit/punctuation any more (AltGr is Ctrl+Alt on Windows). Remapped: Save All Ctrl+Shift+F10, Close All Documents Ctrl+Shift+F11, History Ctrl+Shift+Y, Reference pane Ctrl+Shift+5, Right panel Ctrl+Shift+6, Paste Image Ctrl+Shift+V, Theme Ctrl+Shift+F12, Library 1-6 Ctrl+Shift+F1-F6, Ask AI Ctrl+Shift+Space (was Alt+Space, which opens the Windows system menu), plus Insert/Encoding/Line-ending commands. Reset and Clear Recent now use Ctrl+Alt+Shift+F9/F8.
- Saved shortcut overrides equal to a previous default migrate to the new default; custom shortcuts are kept and never displaced.
- Ctrl+Alt+printable shortcuts are ignored while AltGr is held, and the shortcut editor warns when you record one.
- Caption buttons have tooltips and automation names, the maximize glyph changes to restore, and Close highlights in the danger colour.
- The window fits the working area, scales minimum size with UI scale, maximizes when the default does not fit, and clamps saved sizes. The Library auto-collapses below ~1000 effective px, the chrome condenses below 600 px tall, and first launch on short screens opens in Source view.

### Notification model (SB-01 / U-01) and interim privacy notices (WP00)

- Status messages carry an explicit severity (`ShellViewModel.SetStatus(text, StatusSeverity)`), replacing the keyword heuristic for new code. Info/Success stay at least 6 s + 60 ms per character and are held while the pointer or focus is on the status text; Warning/Error stay until replaced. The status text is a button that opens Activity, and every message is mirrored into Activity. Screen readers receive Polite (Assertive for errors) live announcements.
- Added a non-modal notification banner API (`ShowBanner`/`DismissBanner`) with optional action buttons, and a host UI-dispatcher so background threads post safely.
- The four safety messages (recycled-with-newer-edits, link repaired, updated from disk, historical version opened) now persist as warnings.
- Documentation: corrected the Library move/duplicate/folder claim in the user guide; documented that Copilot prompt text is currently passed on the process command line (with an in-app warning) and that print-preview PDFs are written to `%TEMP%\Tern\PrintPreview`.

### Error boundary, save integrity and history (WP01 / WP02)

- Unexpected exceptions in commands, on the UI thread and in background tasks no longer crash Tern: they appear as an Error in Activity and a non-modal banner ("Something went wrong … Your text is safe.") with Details and Copy report (no document text). A fatal error still triggers a bounded recovery flush. Cancellation is never reported as an error.
- Saving preferences no longer throws; a locked or read-only settings folder shows a warning and settings apply for the session (B-17). A failure in recent-files, history or the search index after a successful write now leaves the save successful and shows "File saved; …" as a warning.
- A listener that throws during a document state change can no longer leave save bookkeeping half updated (D-01). History records exactly the text written to disk (D-05).
- Damaged history entries are set aside (`.json.corrupt`) instead of failing the whole history, saves, or renames; History shows them as unreadable, and the status reads "Saved · N damaged history version(s) set aside". History writes are flushed to disk (B-02). Crash-recovery cleanup and checkpoints tolerate a missing or locked folder (R-03).
- The first save over an existing file, and Keep mine over an external change, keep the replaced version in History with a label (B-06). The History dialog shows labels.
- Optional diagnostic logging (set environment variable `TERN_DIAGNOSTIC_LOG=1`; off by default) writes error types and stack traces, never document text, to `diagnostics.log` in Tern's state folder.

### Metadata, merge and index resilience (WP03 / WP04 / WP05)

- Per-user working context (open tabs, last document, view) is now stored in Tern's local state folder (`workspace-contexts`) instead of `.tern-workspace.json`, so closing or exiting never writes into a shared, synced or read-only workspace folder (S-10). An existing in-file context is migrated once on first open; the descriptor keeps its name and any other metadata, and a read-only descriptor is left untouched while the context still restores. Rolling back to an older Tern simply starts without the saved tabs.
- If session or workspace-layout data cannot be saved when closing a workspace or exiting, Tern now offers Retry, Exit anyway / Close anyway, or Cancel instead of trapping the user in the app. Documents are always saved or kept open first (B-03).
- Merge is refused when the changed file on disk is in an encoding Tern cannot read losslessly; nothing is written, and a warning offers Save my copy as… and guidance to reopen with the right encoding (B-04).
- A failure while processing file changes (including search-index errors) no longer silently stops live updates: Tern shows an Error in Activity, a banner with Rescan and Rebuild actions, and an "Updates paused" note on the Library section; the next change or Rescan recovers, and files missed during the pause are picked up. Workspace notices are now thread-safe (B-05, B-21).

### Overlay contract (WP08 / SB-03, U-03, U-16) and help buttons (WP09 / U-04)

- Escape now closes every in-window overlay (Activity, save/recovery details, Quick Start replay, AI edit review, pickers, panels) and returns focus to the control that had it. Overlays that handle Escape themselves (Quick Open/Command Palette, Write with AI) opt out through `IDismissibleOverlay`.
- The overlay scrim now covers the whole window (title bar, menus, toolbars, status bar). While an overlay is open, registry shortcuts and menu access keys (Alt, F10) are suspended; only Escape and the overlay's own keys work. Retry save / Save as… / Retry recovery in the save details overlay close the overlay before any dialog opens.
- Help buttons ("?", "How version history works", "Learn about tags") no longer reuse command IDs as `Tag` (new `Help.Feature` attached property), so they stay enabled in every state, and their tooltips start with "Help".

### Safe recovery review (WP07 / U-02, B-11, SB-02)

- Review recovered work: "Restore as unsaved copy" is the accent default button (Enter), "Ask later" is Escape/cancel, and "Discard checkpoint" is a separate danger-styled button on the left that requires a second confirmation ("Discard recovered text" / "Keep", Keep default). Lines that differ between disk and checkpoint are highlighted, with a count in the header. The dialog uses the shared dialog frame and action row.
- Unreadable recovery files are reported once with Show files / Delete damaged files / Keep. Delete removes only the unreadable files; Keep moves them to `recovery\damaged` (never scanned again). The prompt no longer returns at every startup.
- Shared `Button.danger` style (hover, pressed, focus, disabled in both themes) and `PromptWindow` `destructiveIndex`, which can never be the default button.

## 1.0.5

See [Tern 1.0.5 release notes](docs/release-notes-1.0.5.md) for onboarding, templates, authoring dialogs, rendering, page selection and Windows distributions.

## 1.0.4 — November 15 2026

- Pin the bundled Windows runtime to .NET 10.0.15 and include its matching upstream license notices, preventing SDK patch updates from causing runtime inventory mismatches during packaging.

### Six-screen usability and shared-style corrections

- Give Ask & learn answers more reading height after a response arrives, keeping a compact question editor available for follow-ups. Hide the redundant connection summary while reading an answer; connection selection and management remain at the top.
- Activate Quick Picker results with one click, including line-ending selection, while retaining keyboard navigation, pending-result guards and separate row actions.
- Enlarge and center the welcome panel, with responsive scrolling on constrained screens and shared theme spacing.
- Remove the raw request/JSON inspection section from Write with AI. Keep the sharing scope, connection destination and explicit Generate for review action visible.
- Show Create content only when there was no selected text and the active scope is Entire note. Selection-based rewriting, summarizing, explanation, task extraction and proofreading remain separate.
- Simplify Review AI suggestion into a complete before/after comparison with three-step guidance, changed-section count, responsive stacking, Back, Copy result, Apply/Insert and Close. Preserve revision checks, one-step Undo and explicit saving.
- Apply the shared themed disclosure template to all expanders, including hover, pressed, keyboard focus and disclosure chevrons. Extend the reflection-checked dialog catalog to Paper and Midnight at 100%, 150% and 200% text scales.


### Latest screenshot and README export fixes

- Move Ask & learn connection selection, connection management, new conversation, copy and sharing information to the top; enlarge the question editor and remove the lower expander clutter.
- Render help answers as native Markdown with headings, emphasis, lists, tables and syntax-coloured code. Commit answer status only after rendering, reveal the newest answer and reject superseded or disposed conversation results.
- Reset export destination, format options, completion status and notes whenever the selected format changes. Invalidate previous results when settings change or another attempt starts or fails; show Open file and Show in folder only after a complete successful current export.
- Align export actions in a responsive footer, place PDF paper and margins together, and collapse export notes to avoid unnecessary completion scrolling.
- Improve TXT export with wrapped prose, compact aligned tables, labelled wide-table records, deduplicated numbered external/relative/section link references, preserved nested lists, code indentation and Unicode; retain an explicit legacy layout option for existing callers.
- Rasterize supported inert SVG badges for PDF and print export through the bounded native SVG reader, including scalar-positioned text spans used by repository badges. Preserve PNG resolution metadata so badges retain their natural physical size. Active content, URI resolution and unsupported SVG features remain blocked.
- Add persistent contextual Next step guidance and installed Learn more links to the main window and shared dialog shell, with operation-specific help and export states and a compact search-results adjustment to preserve reading space.
- Update installed Feature guide and Tutorials, beginner export lessons, README, user and beginner guides, and release notes for these workflows. Regenerate the supplied README HTML, PDF and TXT from its unchanged original Markdown and capture the corrected screens.

### Welcome, navigation and workspaces

- Complete the 25-feature GUI implementation pack, with shared command handling, document revision guards and automated feature checks.
- Add a compact first-note welcome with Source/Reading examples, New/Open/Folder actions, separate unsaved practice notes and templates, a truthful onboarding checklist, dismissal and replay.
- Simplify main-window navigation and the AI entry, preserve pane sizes and visibility, and add layout restoration and Reset layout.
- Distinguish ordinary folders from saved workspaces, detect existing workspace metadata, show multiple roots as expandable stacked trees and preserve tabs when switching roots.
- Unify file/folder creation around a labelled destination and name, with collision checks, stale-target protection and canonical filesystem operations.
- Clarify Close, Forget recent reference and Recycle meanings; keep physical deletion separate from removing a recent item, and improve tab captions, locations and overflow actions.

### Authoring, links, images and document inspection

- Add context-aware heading, list, checklist, quotation, link, image, table and code authoring controls with selection states and command-palette eligibility.
- Provide guided link insertion with inspectable exact destinations, repair details and clearer consequences when renaming linked documents.
- Add image thumbnail, alternative-text, destination and Link/Copy guidance; improve collision retry, attachment importing and save-location handling for new notes.
- Show structured image failures and host-provided Locate, Retry and Review actions; improve bounded image inspection and accessible image actions.
- Clarify table row counts, including the header, enforce numeric bounds and preview the result before inserting it as one undoable edit against the captured revision.
- Improve code language labels, syntax colours and copy feedback; keep unknown languages as plain text without executing examples.
- Theme diagrams consistently and add an enlarged diagram view with zoom, fit, reset and Escape; provide supported math and diagram-source guidance.
- Add safe edits for supported tags, retain a Source fallback for complex metadata and show the current section in the outline.
- Make preview task checkboxes edit the exact current Markdown marker with stale/read-only guards, Undo and explicit saving; improve workspace task filters and counts.

### Reading, search and replacement

- Improve Source, Split, Reading and reference controls, synchronized-scroll preferences and revision-safe restoration of modes and reading positions.
- Replace the inconsistent Find behavior with one document-search flow across Source, Split and Reading, including persistent highlights for all matches and a distinct active result.
- Reveal the active result automatically, provide Next/Previous navigation, selectable result snippets, match counts, wrap feedback and clear no-result states.
- Present case, whole-word and regular-expression options in a compact themed popup; report invalid expressions, source-only matches and bounded result limits clearly.
- Preserve source-to-rendered mappings for escaped text, entities and line endings; reveal matches inside long paragraphs, code scrollers and collapsed details.
- Cancel superseded searches and reject stale query, document, tab and workspace results so old highlights or results cannot replace current ones.
- Preserve read-only protections, captured revisions and one-step Undo for replacement; replacing text never saves implicitly.
- Improve Quick Open and workspace-search loading, completion, cancellation and empty states, with safe result presentation and rejection of late results from a previous scope.
- Correct reading scrollbar behavior while variable-height blocks are measured: thumb dragging retains its proportional document position, both ends remain reachable and release does not snap back.
- Use a slimmer reading scrollbar with a minimum usable grip; retain wheel, track, Page Up/Page Down and keyboard navigation, including synchronized Source/Split interactions.
- Keep bounded parsing off the UI thread during mode changes and discard stale results by generation and revision instead of interrupting the active document's render.

### Export, print preview and output fidelity

- Replace separate Export as HTML and Export as PDF menu entries with File → Export as… (`Ctrl+Shift+E`), offering HTML, PDF and plain text in one window; migrate existing export shortcut overrides.
- Rebuild the export form with the shared theme, labelled format/destination/options, a larger default size, contextual guidance and one primary Export action.
- Keep completion status, Open file, Show in folder and closing/export actions outside the scrolling form so successful export does not push them below the viewport.
- Use a concise completion message while retaining the full output path in its inspectable field/tooltip; preserve full warnings and explicit incomplete-output notices.
- Capture the document and selected options once, prevent repeated concurrent exports and source-file replacement, and keep output-opening errors separate from export success.
- Replace the old plain-text printing route with a larger resizable formatted Print preview (`Ctrl+P`), followed by Open printable PDF for exact pagination and printer selection in the installed PDF reader.
- Keep print/export snapshots independent of later edits, preserve source Markdown and refuse incomplete printable output.
- Fix Windows HTML stylesheet CSP hashes by hashing the same normalized line endings browsers use, restoring exported page styling without enabling scripts or unsafe inline styles.
- Preserve semantic headings, emphasis, lists, quotations, tables, code, permitted images and document bookmarks; improve printed HTML table headers, wrapping, page-break behavior and light-paper colours.
- Default desktop HTML export appearance to the current app theme while retaining explicit Light, Dark and Auto choices.
- Share the native preview's bounded lexical highlighter with HTML/PDF export so known-language code retains syntax colours; keep code escaped and unknown-language content plain.
- Wrap PDF code using measured glyph widths, preserve indentation and avoid splitting Unicode surrogate pairs at line boundaries.
- Add installed-font fallback for emoji and symbols in prose and code, additional symbol coverage, Japanese text and circled numbers; report characters the installed fonts cannot display.
- Correct supplementary-character PDF Unicode maps so supported emoji remain searchable and copyable rather than becoming missing or replacement characters.
- Keep plain-text output readable through heading separators, lists, code indentation and table layout; omit document-properties front matter in the desktop output consistently and explain that TXT cannot contain fonts, colours or embedded images.
- Verify both supplied large-document export cases against their original Markdown paths so relative local images resolve correctly and originals remain unchanged.

### Help, tutorials and AI connections

- Simplify Ask & learn around a Tern question, examples, response and optional connection/privacy details; remove the selected-note attachment control and keep notes, selections and paths out of help requests.
- Replace exact all-word topic matching for tutor questions with ranked installed-guide retrieval, normalized task vocabulary and reduced weight for conversational filler.
- Accept naturally phrased Tern questions, including “I need to add a new AI connection, how can I do that?”, and prioritize general setup guidance unless a specific provider is named.
- Automatically display a labelled installed-guide answer when no Help connection is configured, generation fails or the provider times out; preserve the question for retry and bring the answer into view.
- Keep unrelated questions outside the tool-help scope, ground AI answers in installed guide facts and commands, and derive lesson/tool actions from the trusted catalog rather than model-provided commands.
- Retain bounded conversation history, exact question text, Copilot request-length limits, cancellation, request lifetime and stale-provider/generation guards.
- Add searchable in-app tutorials for all-mode Find, unified export, formatted print preview and automatic help recovery; update existing beginner lessons, menu paths, prerequisites and effective shortcut descriptions.
- Provide a pausable practice walkthrough with safe lesson actions and a Help connection choice independent of the writing connection.
- Improve credential-free connection summaries for capability, destination, model, state and validation time; invalidate cached checks when configuration or credentials change and avoid silently substituting another connection after deletion.
- Remove Copilot CLI remote-session switches rejected by the installed CLI; retain isolated working/configuration directories, disabled custom instructions/hooks and denied file, shell, URL and memory tools.
- Add the explicit Copilot client-telemetry opt-out and retry recognized transport failures once; do not retry authentication/entitlement failures or silently change models, credentials or account permissions.
- Preserve explicit writing scopes and original/result AI review, instruction-only drafting, insert/replace choices, rejected stale replies, passage exclusions and verifiable workspace citations.

### Recovery, activity, settings and interface consistency

- Distinguish saving a note from creating a recovery checkpoint; show persistent save/recovery failures with details, retry and Save copy against the captured document.
- Make merge conflicts explicitly unreviewed until a choice or manual review is made, label comparison panes and protect acceptance against stale document state.
- Clarify cached, fresh and portable URL-open choices; avoid requests just from opening the dialog, guard refreshes by revision and display provenance, warnings and measured asset progress.
- Add typed, bounded activity history with operation identity, phases, counts, outcomes, safe cancellation and inspectable/paged error details.
- Keep Settings changes pending until Apply/Done, support Cancel/reset and font previews, and preserve retry/rollback when settings cannot be saved.
- Apply shared Paper/Midnight resources, typography, spacing, borders, controls, focus routes and disabled states across dialog families; improve reflow and action visibility at compact sizes and larger text scales.
- Remove unwanted black hover surfaces from shortcut/expander headers and use the same themed header pattern for collapsible help and connection details.
- Constrain AI/search popup measurement to the available window, remove horizontal overflow, wrap long connection details, collapse optional details and keep primary/secondary actions visible.
- Correct Avalonia compiled bindings whose data context could not be inferred, provide the shortcut-header data type and use instance handlers for folder collapse and inline-rename focus events.
- Improve nullable selection guards in authoring and picker workflows; keep SDK-discovered partial view files included in the desktop build.

### Documentation, versioning and verification

- Update README, the user and beginner guides, feature/tutorial content, release guidance and website presentation to reflect the current commands and workflows.
- Update current application version, About fallback, main-window version label, Windows manifest, MCP client metadata and dependency inventory from 1.0.3 to 1.0.4; preserve historical release/version records.
- Add regression coverage for all-mode search and navigation, replacement Undo, compact popups, dialog layouts, help scope/recovery, CLI arguments/retries, large-document scrollbar dragging and export fidelity.
- Make the remote-image concurrency test wait for controlled in-flight downloads rather than depend on thread-pool timing; retain production download limits, timeouts and separate cancellation/timeout checks.
- Retain the offline/security audit and narrowly recognize the exact telemetry-disabling CLI switch as an opt-out rather than telemetry use.
- Generate actual Avalonia PNG screenshots in both themes and multiple text scales, a real-browser HTML capture, exported PDF sample pages and contact sheets; keep screenshots separate from `.trx` test reports.
- Audit all 669 regenerated PDF pages for empty pages, page-boundary clipping and replacement characters; confirm HTML stylesheet hashes, heading/bookmark counts, workbook images and searchable international/code symbols.
- Record final automated results and artifact locations in the 1.0.4 verification report. Physical printer, screen-reader, physical-DPI and clean-machine checks remain separate native acceptance work.
- Pass the final unfiltered Release solution suite with **3,051 tests, zero failures and zero skips**, plus **27 Python release-automation tests**; repository structure/metadata checks and patch whitespace checks also pass.
- Keep external-image omissions and unsupported formats visible in export warnings; an offline evidence run does not claim to have fetched the SQL guide's external badges. Automatic help fallback does not claim to repair provider credentials or entitlements.

## 1.0.3 — November 5 2026

- Add AI edit review with original/result comparison, Back, Copy result, Apply and Close, plus Escape to discard and close.
- Improve selection-aware AI prompts, quick actions, workspace answers with citations and related-note suggestions.
- Refresh library shortcuts, outline, searchable document links and version history with clearer guidance.
- Improve keyboard focus and tab navigation, search-match visibility and empty-tab-bar double-click creation.
- Reduce duplicate workspace scans, coalesce watcher events, reuse highlighting state and move costly work off the UI thread.
- Bound merge memory and file reads; improve online download concurrency, disk-backed assets and cache cleanup.
- Reuse short-lived AI connection validation with configuration and credential checks.
- Add optional Authenticode signing and verification tooling. This release remains unsigned because a trusted signing identity is not configured.
- Update Feature Guide and Tutorials, user documentation and the performance before-and-after report.
- Update application metadata, Windows manifest, displayed version, MCP client metadata and dependency inventory to 1.0.3.

## 1.0.2 — November 1 2026

- Unify saved API, GitHub Copilot and MCP connections in the service selector and main toolbar, with create, edit, delete, validation and secure credential storage.
- Keep saved-profile selection on Choose service until Configure connection is clicked, consistently across all service types.
- Validate AI connections with an explicit fixed test message and show actionable authentication, model and connectivity errors.
- Add saved Copilot note-writing connections and a dedicated MCP tool workflow with individually reviewed tool calls.
- Enlarge the AI writing workspace, keep actions visible at compact sizes, and preserve cancellation, document revision checks and Undo.
- Add official Copilot and MCP setup links and update the Feature Guide, tutorials and beginner instructions.
- Bound Copilot guide requests to its supported prompt limit while preserving the exact user question.
- Disable warnings-as-errors and suppress compiler/analyzer warnings solution-wide as requested.
- Update application, Windows manifest, displayed version, MCP client metadata and package inventory to 1.0.2.

## 1.0.1 — October 31 2026

- Make linked images and download badges receive clicks, and make the entire visible text link clickable, including its final character.
- Retain cross-document heading targets until the destination preview is ready, and keep queued source scrolling from overriding link navigation.
- Report missing heading targets in the status bar.
- Recheck workspace watcher deadlines before flushing queued timer callbacks, and keep the native burst test independent of thread-pool scheduling.
- Update application, Windows manifest, displayed version, MCP client metadata and package inventory to 1.0.1.

## October 31 2026 rebuild verification

- Make the temporary Windows file-reader test release its handle on a dedicated thread, independently of the test runner's thread pool. Keep the atomic-save retries and success assertions unchanged.
- Verify that a persistent reader causes a bounded save failure while preserving the original metadata and removing the temporary file.
- Keep the application version at 1.0.0. Trusted signing remains unavailable; rebuilding does not resolve Unknown publisher or SmartScreen reputation warnings.

## Image Link and Copy choice

- Make Insert Local Image use copied image files directly, applying the existing same-folder or Link/Copy rules without asking for their source again; retain the source picker when no image file is copied.

- Unify Paste Image as Attachment with drag-and-drop, Insert Local Image and Ctrl+V for copied files: adjacent files link immediately; other locations offer Link/Copy/Cancel before any destination picker. Only clipboard pixels without a source file go directly to choosing a copy location.

- Link image files from the Markdown file's own folder immediately when dropped, selected or pasted with Ctrl+V, without a dialog; retain Link/Copy choice for other locations.

- Add Link original / Copy image / Cancel for dropped, selected and ordinarily pasted image files, including subfolders and unrelated locations.
- Link inserts the original relative path without making a copy. Copy writes to the selected folder and filename and links that copy; existing files remain protected.
- Paste Image as Attachment follows the common rules for copied files. Clipboard pixels without a source file open the destination picker directly.
- Remember exact external image selections per note for preview and export after reopening, without granting access to entire folders.

## October 30 2026 review fixes

- Enable Save and Save All for empty drafts and retain explicitly chosen draft filenames.
- Save new notes before adding pictures and continue the insertion automatically; cancellation preserves the draft.
- Carry current local attachments and linked Markdown dependencies on Save As, reject conflicting bundles, and retain resource locations through recovery and subsequent checkpoints.
- Allow confirmed UTF-8 copies of encoding-protected documents while protecting the original bytes.
- Keep text-box editing shortcuts within the focused input and clear conflicts when disk content returns to its baseline.
- Restore duplicate tabs in order, including the active instance and separate caret/scroll positions.
- Require a new destination after export format changes, reject incompatible filename extensions, and report HTML/PDF truncation.
- Include the beginner guide, practice note, issue matrix, validation record and Windows x64 publish script. See [current verification](docs/BEGINNER_REVIEW_VALIDATION.md) for completed checks and remaining limits.

## 1.0.0-rc.2 (release preparation; not stable acceptance)

- Improve Markdown presentation against the supplied guide repositories: centered introductions, HTML anchors, code token colors, front-matter tables, wrapped/routed diagrams and entity relationships.
- Add the user-requested image-only live download service and bounded native SVG image rendering for actual linked screenshots and badges; each request fetches fresh bytes without disk caching.

- Correct reviewed merge preconditions, recover into independent unsaved copies, and preserve competing save versions with explicit failure reporting.
- Track saved encoding/BOM policy through undo and save completion; validate recovery bounds and serialize checkpoint writes.
- Bind replacement results to document/revision/options; repair newline matching, repeated link rows, external-change integration and large unsaved search overlays.
- Protect export and Save As/copy destinations, provide bounded staged local image insertion and preserve source through failed transactions.
- Bound reconstructed HTML trees, coordinate preview cancellation and own/dispose bounded raster images.
- Improve safe prompt dismissal, recovery comparison/provenance, per-document profiles, navigation/focus, theme contrast and compact headers.
- Pin SDK/dependency lockfiles and generate source manifests, dependency/SBOM/license inventory and release evidence.
- Stable-release acceptance, including signing and clean offline/native accessibility validation, remains open in docs/V1_RELEASE_LEDGER.md. No previous test total is represented as acceptance for this candidate.

## 1.0.0-rc.1 (unreleased pre-release)

### Added

- Complete editor shell: tabs, source/split/reading modes, workspace explorer, outline, backlinks / broken links, statistics, command palette, quick open, find/replace, workspace search.
- Native GFM preview with sanitized HTML, math, Mermaid (flowchart + sequence).
- Safe save pipeline, encoding/line-ending preservation, external-change detection with three-way merge, crash recovery.
- HTML, PDF, plain-text export.
- `tests\Tern.Security.Tests` (172 tests): path traversal / junction, link policy, XSS corpus, hostile inputs, state-file corruption, offline (no-network) assembly audit.
- Desktop dialog/flow tests (settings, About, encoding, export end-to-end, merge window, palette pixel test).
- `tests\Tern.Benchmarks` Stopwatch benchmark console; `scripts\publish.ps1`.
- Documentation set under `docs\`.

### Fixed

- Command palette input rendered black on hover/focus (Fluent `PART_BorderElement` template); now panel-coloured.
- **Security:** workspace file operations (create/rename/move/duplicate) checked containment lexically only, so a directory junction inside the workspace pointing outside allowed operations outside the root. Containment now resolves link targets; duplicating a directory link is rejected.
- **Robustness:** `AppSettings.Normalize` let NaN/infinity through `Math.Clamp`; non-finite values now fall back to defaults.
- Settings dialog numeric fields now clamp to valid ranges and settings are normalized after the dialog.

### Changed

- Version set to `1.0.0-rc.1` in `Directory.Build.props`.

## 2026-10-26 quality pass (still 1.0.0-rc.1)

- Integrated title bar, vector navigation/tree icons, reference-aligned Midnight colors and compact header handling.
- Added focus mode, keyboard pane cycling and a resizable read-only document reference pane.
- Added bounded local saved-text history with comparison and restoration into an unsaved copy.
- Wired single broken-link repair to the inspector as an undoable source edit.
- Moved preview model construction off the UI thread and rejected stale results; fixed workspace event subscription accumulation.
- Fixed file-drop routing and added a routed-event regression test. Explorer drag and drop remains unverified.
- Published ReadyToRun Windows binaries and built an unsigned MSIX with file-association declarations. Signing and installation remain open gates.
- Full automated regression: 1,628 passed, 0 failed, 0 skipped. Startup proxy measurements and qualifications are in docs/PERFORMANCE.md.

## 2026-10-26 writing desk redesign (still prerelease)

- Replaced the sidebar action strip with a Library Add menu and workspace card.
- Added workspace, folder-document and all-document close workflows, visible close buttons and context menus. Save/Discard/Cancel decisions protect unsaved work; closing never deletes files.
- Connected window close to the save decision workflow and persisted session paths before tabs are removed.
- Reworked toolbar hierarchy, canvas framing, Midnight/Paper themes, welcome/compact states, preview spacing and table-heading widths.
- Published the preceding File menu, file/folder creation, Windows STA startup and text-printing work as well.
- Fixed the Quick Open / Insert Link shortcut clash.
- Validation: 1,643 automated tests passed; native OS interaction acceptance remains open.
