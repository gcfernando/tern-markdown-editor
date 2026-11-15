# Changelog

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
