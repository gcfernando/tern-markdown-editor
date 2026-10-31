# Version 1 release acceptance ledger

Tern's application metadata is **1.0.1**. This ledger restores the path referenced by the historical changelog and records current acceptance boundaries. It does not retroactively verify historical builds or mark a pending candidate stable.

| Gate | Current evidence requirement |
| --- | --- |
| Reproducible automated gate | Successful locked restore, warnings-as-errors build and filtered eight-project test run for the exact commit; inspect reports rather than assuming completion |
| External sample/corpus tests | Run the eight explicitly excluded cases with their original inputs/native environment; see the [case list](BEGINNER_REVIEW_VALIDATION.md#automated-gate-and-external-cases) |
| Package integrity | Fresh complete Windows x64 ZIP, matching checksum and extracted-application smoke evidence |
| Editing and file safety | Native save/Save As/conflict/recovery checks with synthetic copies and unchanged unrelated files |
| Offline operation | Clean package in an offline environment using local-only notes/assets; test cached URL bundles separately |
| Native interaction | Real clipboard/drop combinations, printers, keyboard/focus and screen-reader output |
| Display | Multiple Windows scales, window sizes, themes and monitor arrangements |
| Export fidelity | Inspect representative HTML/PDF/text, glyph coverage, diagrams, images and truncation notices |
| Performance | Reproducible representative inputs and environment; no 1 GB editing claim |
| Optional connections | Synthetic live provider/CLI/MCP checks only where authorized; local fixtures do not certify remote services |
| Publisher trust | Current route is unsigned; no trusted signing identity or warning-free guarantee |

For a release, record the source commit, package SHA-256, Windows environment, actual run/test counts, excluded/skipped cases and native checks completed. A preview can be published with clear limitations after its automated checks pass; that does not close every row above. See [releases](releases.md) and [privacy](privacy.md).
