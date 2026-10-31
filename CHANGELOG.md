# Changelog

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
