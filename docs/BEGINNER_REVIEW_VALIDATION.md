# Feature inventory and validation

This is the current review scope for Tern's tracked checkout, not a certificate of stable-release acceptance. Application version remains **1.0.2**. Historical changelog counts describe earlier reviews and must not be reused as results for a new source/package snapshot.

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

The unfiltered solution contains tests that need material or OS interaction absent from a clean hosted checkout. The reproducible workflow reads `tools/automated-test-filter.txt` and excludes **eight cases**, while retaining the original tests/assertions for separate acceptance:

| Excluded group | Cases | Required environment/input |
| --- | --- | --- |
| `Tern.Desktop.Tests.RenderingCorpusModeTests` | 1 | Original untracked rendering corpus/inventory |
| `Tern.Desktop.Tests.Question35SampleTests` | 2 | Exact supplied external Markdown sample |
| `Tern.Desktop.Tests.NativeRecycleRestoreTests` | 1 | Interactive Windows Shell/recycle-bin restore |
| `CorpusValidationTests.Every_inventoried_document_preserves_semantic_blocks_and_realizes_native_preview` | 1 | Original rendering corpus inventory |
| `CorpusLocalAssetTests.Every_corpus_image_records_actual_native_load_or_explicit_policy_block` | 1 | Original corpus images |
| `ScrollbarDragTests.Held_left_mouse_thumb_drag_moves_down_and_back_up_without_snapback`, `suppliedSample: True` | 2 | Supplied sample at widths 400 and 760 |

The synthetic scrollbar cases (`suppliedSample: False`) remain in the gate. A pre-existing remote-asset skip must be reported if present in a run. Exclusion is not a pass, and the filtered gate is not the full unfiltered suite.

This page does not assert that a pending run succeeded. Use the [workflow run](https://github.com/gcfernando/tern-markdown-editor/actions/workflows/release.yml), its TRX/evidence artifacts and package manifest for the exact source commit, test totals, exclusions and smoke results. Record genuine failures and fixes rather than changing a valid assertion solely to obtain a green result.

## Native and release acceptance

Still verify actual clipboard/file-drop payloads, physical printing, multiple DPI/scaling layouts, keyboard/screen-reader behavior, clean extracted startup, installed offline local-only editing, attachment portability and representative performance. Signing status remains unsigned. Details are in the [release ledger](V1_RELEASE_LEDGER.md) and [review matrix](review/oct30/ISSUE_MATRIX.md).

Screenshots supplied under `Images` document the visible interface, not successful testing of every shown feature. The [practice note](practice/practice-note.md) mirrors the built-in practice text and contains no remote image. The [beginner guide](BEGINNER_GUIDE.md) explains how to use it safely.
