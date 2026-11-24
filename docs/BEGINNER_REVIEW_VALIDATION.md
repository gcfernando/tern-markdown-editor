# Feature inventory and validation

This is the current review scope for Tern's tracked checkout, not a certificate of stable-release acceptance. Application version remains **1.0.5**. Historical changelog counts describe earlier reviews and must not be reused as results for a new source/package snapshot.

## Source understanding

The documentation review structurally inventoried 482 authored source, test and script text files (103,011 lines at the review's starting snapshot), decoding the full text and indexing declarations, test names and documentation references. Binary/generated material was separately identified. Targeted semantic reads covered startup, commands, beginner lessons, file/save/recovery flows, workspace scanning, rendering/resource budgets, exports, settings, credentials, URL/image requests and optional AI/MCP/Copilot adapters.

That is broad source coverage plus targeted detailed review, **not** a claim that every line received an independent correctness or security audit. Later helper and application fixes can change the counts. Repository test-fixture Markdown remains input data and was not rewritten as product documentation.

| Feature area | Source/test evidence | Remaining boundary |
| --- | --- | --- |
| Beginner lessons and commands | Lesson catalog, practice text, command registry and headless flows | Physical keyboard, focus and assistive technology |
| Saving, external changes and recovery | Filesystem/domain/app tests and transaction/recovery code | Real filesystems, concurrent editors and backup practice |
| Preview, diagrams and resources | Rendering/preview/security tests and bounded native renderers | Untracked external corpus and representative large/hostile inputs |
| URL import and images | Guarded loaders, bundle logic and local fixtures | Real network/offline environments; complete cached assets |
| AI/MCP and credentials | Protocol fixtures and explicit request/credential code | Live services, entitlement, retention and installed CLI compatibility |
| PDF/HTML/text/print | Export tests, readiness and native text-print implementation | Actual glyph coverage, printers and output inspection |

## Automated gate and external cases

On 2026-11-21, the user approved removing the eight cases tied to unavailable original corpus/sample files and then explicitly confirmed removing the one skipped external-payload remote-image acceptance case. These nine removed cases are not passing acceptance evidence. Generalized rendering/image coverage and the synthetic scrollbar case remain. The reproducible workflow reads `tools/automated-test-filter.txt`; its single clause excludes only the interactive Windows Shell case:

| Excluded group | Cases | Required environment/input |
| --- | --- | --- |
| `Tern.Desktop.Tests.NativeRecycleRestoreTests` | 1 | Interactive Windows Shell/recycle-bin restore |

The synthetic scrollbar case remains in the gate without an external-file parameter. The external-payload remote-asset skip was removed with explicit approval; general remote-image tests remain. Exclusion or removal is not a pass, and the filtered gate is not the full unfiltered suite.

The native Windows recycle/restore case passed on 2026-11-21 using only its GUID-named synthetic file, including in the complete unfiltered run before the eight original-input cases were removed. The missing original corpus/sample inputs remain unverified, but their eight tests are no longer part of the agreed automated suite. A previous offline disposable Windows Sandbox also passed actual MSI installation, installed-payload hashes, native edit/save/forward/restart for installed/portable/standalone forms, and uninstall while preserving unrelated synthetic Markdown registrations. These results do not establish current-package acceptance or unexecuted DPI, physical input, screen-reader, clipboard or printer acceptance.

This page does not assert that a pending run succeeded. Use the [workflow run](https://github.com/gcfernando/tern-markdown-editor/actions/workflows/release.yml), its TRX/evidence artifacts and package manifest for the exact source commit, test totals, exclusions and smoke results. Record genuine failures and fixes rather than changing a valid assertion solely to obtain a green result.

## Native and release acceptance

Still verify actual clipboard/file-drop payloads, physical printing, multiple DPI/scaling layouts, keyboard/screen-reader behavior, clean extracted startup, installed offline local-only editing, attachment portability and representative performance. Signing status remains unsigned. Details are in the [release ledger](V1_RELEASE_LEDGER.md) and [review matrix](review/oct30/ISSUE_MATRIX.md).

Screenshots supplied under `Images` document the visible interface, not successful testing of every shown feature. The [practice note](practice/practice-note.md) mirrors the built-in practice text and contains no remote image. The [beginner guide](BEGINNER_GUIDE.md) explains how to use it safely.

The [current GUI task guide](gui-tasks.md) documents the implemented controls and synthetic screenshots. Native screen-reader, physical-DPI, printer, clipboard and clean-machine startup checks still require verification on Windows.
