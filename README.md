# Tern

**Start here:** [Beginner guide](docs/BEGINNER_GUIDE.md) · [Practice note](docs/practice/practice-note.md) · [Feature inventory and validation](docs/BEGINNER_REVIEW_VALIDATION.md).

**Current review:** [October 30 issue matrix](docs/review/oct30/ISSUE_MATRIX.md). The Release development executable is built at `src/Tern.Desktop/bin/Release/net10.0/Tern.exe`; keep its bundled files together and install the matching .NET runtime. See the validation record for checks actually completed on this source snapshot.

Tern is a local-first Markdown editor for Windows built with .NET 10 and Avalonia 12. It opens ordinary `.md` files with no conversion and uses local files and bundled assets for document processing. No account or telemetry service is required. Installed offline operation remains a release acceptance gate.

Open from URL downloads Markdown and its directly referenced supported images/files to a managed cache and rewrites their references. Complete cached bundles reopen offline; Download and Open and Save As keep portable assets beside the local note. Partial downloads identify missing assets and retry on reopening. Ordinary remote image links in other local notes still use the bounded live image loader. Scripts and active web content are not executed.

**Version metadata:** 1.0.0. This working-tree review build is not release-certified. Current usability work, evidence and outstanding acceptance checks are recorded in the [review validation](docs/BEGINNER_REVIEW_VALIDATION.md).

## Highlights

- Source editor (AvaloniaEdit) with Markdown highlighting; Source / Split / Reading modes; tabs.
- GFM preview rendered natively (no web view): tables, task lists, footnotes, code, images, front matter, LaTeX-style math, Mermaid flowcharts and sequence diagrams.
- Workspace explorer, outline, backlinks and broken-link lists, document statistics, quick open, command palette, find/replace, SQLite full-text workspace search.
- Safe saving: atomic replace, encoding/BOM preservation, selectable LF/CRLF/CR line endings, external-change detection with a three-way merge dialog, and crash recovery checkpoints. New documents default to CRLF; opening a file preserves its text and attributes.
- Export to HTML (self-contained), PDF (bundled PDFsharp, offline) and plain text.
- Library with explicit Add destinations, expandable folder/workspace roots, document tabs, resizable writing canvas and optional inspector.
- Themes (light/dark), focus mode, a read-only reference pane, local saved-text history, keyboard shortcuts and settings.
- Visible File/Edit/Format/View/Help menus, undoable broken-link repair and Windows text printing.
- Library Files × closes the associated tabs and removes the reference after save decisions; Cancel preserves both. Recent and Quick Open reference removal keeps open tabs. Neither operation deletes saved files.
- HTTP/HTTPS Markdown import through File or + Add → Open from URL…, with public GitHub file-link conversion and explicit localhost support. Online references persist in Recent for deliberate reload; imports open editable unsaved copies.
- Fifteen beginner lessons, including pictures, printing, recovery and a practice note, in a guide that stays open while you practise.
- Shared HTML/PDF/Print readiness, safe first-open replacement of the startup draft, ordinary image paste, source-area file drops and a complete Format menu.
- Save and Ctrl+S support empty drafts. Adding a picture to a new note opens Save, then continues insertion. An explicit draft filename takes priority over its first heading.
- Save As copies referenced local files and their Markdown dependencies inside the document folder, preserving their current bytes. Conflicting attachments are kept and the save is refused. Bundles are limited to 512 files, 64 MiB per file and 256 MiB total. Existing references outside that folder require keeping the copy in its original folder or moving those attachments inside first.
- Encoding-protected documents can be saved as a separate UTF-8 copy after confirming that the displayed text may contain replacement characters. The original bytes remain protected.
- Dropping, selecting or pasting an image file beside the Markdown file links it immediately without a dialog. Images from other folders ask Link original / Copy image / Cancel. Link uses the original location; Copy asks for a destination and links the saved copy. Paste Image as Attachment uses the same rules for copied files; clipboard pixels without a source file need a copy destination. Selected external image files retain their preview permission per note when reopened; other files in their folders remain outside that permission.
- Insert Local Image uses copied image files from the clipboard before opening a source picker. Their locations determine whether to link immediately or offer Link/Copy. When the clipboard has no image files, the regular file picker remains available.

## Build, test, run

```powershell
dotnet build Tern.slnx -c Release -m:1
dotnet test Tern.slnx -c Release -m:1
dotnet run --project src\Tern.Desktop -c Release
```

Requires the SDK pinned in `global.json`. Packages are pinned centrally in `Directory.Packages.props` and dependency lockfiles; warnings are errors. V1 distribution targets Windows x64.

## Publish (self-contained win-x64)

```powershell
.\scripts\publish.ps1        # -> artifacts\publish\win-x64\Tern.exe
```

## Benchmarks

```powershell
dotnet run --project tests\Tern.Benchmarks -c Release            # full
dotnet run --project tests\Tern.Benchmarks -c Release -- --quick # smoke
```

Results are written to `artifacts\benchmarks\`. Performance and installed-build acceptance remain separate from the current usability checks.

If package vulnerability feeds are unavailable, the normal build can fail with `NU1900` because warnings are errors. For local offline verification with already cached packages, pass `-p:NuGetAudit=false` on that command only. This skips the dependency vulnerability check for that run; it is not a release audit.

## Layout

| Path | Purpose |
|---|---|
| `src\Tern.Domain` | Pure document model, text format, diff / three-way merge |
| `src\Tern.Filesystem` | Load/save with encoding preservation, change monitor |
| `src\Tern.Markdown` | Markdig pipelines, analysis, formatting, structured edits |
| `src\Tern.Rendering` | Preview tree, sanitizer, resource resolver, math, Mermaid |
| `src\Tern.PreviewView` | Avalonia preview control |
| `src\Tern.Export` | HTML / PDF / text exporters |
| `src\Tern.Workspaces` | Scanner, watcher, link graph, file operations, stores |
| `src\Tern.Search` | SQLite FTS index, queries, find/replace, quick open |
| `src\Tern.Recovery` | Recovery checkpoints |
| `src\Tern.App` | View-models, commands, settings (UI-framework independent) |
| `src\Tern.Desktop` | Avalonia application (assembly `Tern`) |
| `tests\*` | Unit, integration, headless UI, security tests and benchmarks |

## Documentation

[Beginner guide](docs/BEGINNER_GUIDE.md) · [Practice note](docs/practice/practice-note.md) · [Feature inventory and validation](docs/BEGINNER_REVIEW_VALIDATION.md) · [Coordinated implementation plan](docs/review/IMPLEMENTATION_PLAN.md) · [CHANGELOG](CHANGELOG.md)

## Closing documents and workspaces

- Use the × on a document tab or open file in the Library to close that document (Ctrl+W).
- Use the × on the workspace card, or File → Close Workspace (Ctrl+Shift+W), to close its documents and release the workspace. Unrelated open documents stay open.
- Use the × on an expanded folder, or its context menu, to close its open documents and collapse it. Its folder entry remains part of the workspace.
- File → Close All Documents (Ctrl+Alt+W) closes tabs while keeping the workspace.

Save/Discard/Cancel decisions are completed before a group is closed. These actions never delete documents or folders. The window close button also asks about unsaved changes.

Use **+ Add** for Open Files, Open Folder, New File and New Folder. Quick Open is Ctrl+K; Insert Link is Ctrl+Shift+K; Print is Ctrl+P.

**Folders and workspaces:** Open Folder browses a physical directory without creating metadata. Create Workspace creates a new named directory containing `.tern-workspace.json`; Open Workspace restores that context. Existing legacy workspace/session records remain compatible. Independent roots stack in the Library, while Company/HR stays nested. Context creation targets its clicked directory; global Add asks whether to use the selected location or a separate outside location.

## Known limitations (summary)

The current editor eagerly loads files up to a 64 MiB safety ceiling; larger files are refused before allocation. **1 GB editing is not supported and is excluded from this implementation scope.** The ceiling is a memory guard, not a responsiveness guarantee. Optional AI Connections sends explicitly reviewed text through OpenAI-compatible, Ollama or Claude Messages protocols. Saved connection keys use Windows Credential Manager; unsaved keys remain in memory. MCP supports explicitly reviewed tool discovery/calls using its documented compatibility protocol. Live provider compatibility remains unverified.

See the [validation record](docs/BEGINNER_REVIEW_VALIDATION.md) for what was actually tested. Native Windows printing produces readable text; PDF export has font/Unicode limits. Native clipboard/drop combinations, printers, Windows display scaling, assistive technology, installed offline operation, performance and signing still require broader acceptance. Saves preserve detected displaced competing versions and report failures explicitly; this is not universal filesystem compare-and-swap. No stable-release claim is made.
