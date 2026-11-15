---
layout: default
title: Tern documentation
permalink: /documentation/
---

# Tern documentation

Tern edits ordinary Markdown files on Windows. Start with the practical guides, then consult the references for saving, rendering, network access or development.

## Start here

| Guide | Purpose |
| --- | --- |
| [Overview and download](README.md) | Features, package requirements and first steps |
| [Beginner guide](docs/BEGINNER_GUIDE.md) | Fifteen short lessons |
| [Practice note](docs/practice/practice-note.md) | A small Markdown file to copy, edit and save |
| [Current GUI task guide](docs/gui-tasks.md) | Current controls, task screenshots and verification limits |
| [User guide](docs/user-guide.md) | Editor modes, files, attachments, workspaces, export and shortcuts |

## Data, safety and support

| Guide | Purpose |
| --- | --- |
| [Privacy and network access](docs/privacy.md) | Local data, URL import, remote images and optional connections |
| [Downloads and releases](docs/releases.md) | Checksums, unsigned packages, updates and acceptance limits |
| [Security policy](SECURITY.md) | Private vulnerability reporting |
| [Validation record](docs/BEGINNER_REVIEW_VALIDATION.md) | Source-review scope and execution evidence boundaries |
| [Release acceptance ledger](docs/V1_RELEASE_LEDGER.md) | Remaining native and clean-machine checks |

## Maintain the project

| Guide | Purpose |
| --- | --- |
| [Development](docs/development.md) | SDK, locked restore, tests, architecture and benchmarks |
| [Performance scope](docs/PERFORMANCE.md) | Benchmark commands and limits on performance claims |
| [Review matrix](docs/review/oct30/ISSUE_MATRIX.md) | Known limits and acceptance work |
| [Implementation and validation plan](docs/review/IMPLEMENTATION_PLAN.md) | Current maintenance and release checklist |
| [Contributing](CONTRIBUTING.md) | Focused changes and required validation |
| [Repository standards](REPOSITORY_STANDARDS.md) | Documentation and maintenance checks |
| [Changelog](CHANGELOG.md) | Historical changes; old results are not current verification |

The [published site](https://gcfernando.github.io/tern-markdown-editor/) documents the desktop application; it does not run the editor in a browser. Source, fixtures and build scripts remain in the [GitHub repository](https://github.com/gcfernando/tern-markdown-editor). See the [MIT license](LICENSE) and [third-party notices](THIRD-PARTY-NOTICES.md).


### Version 1.0.4 workflows

- **Find:** use Edit → Find to highlight matches in Source, Split or Reading. Move with Next/Previous or select a result to reveal it. Review replacements before applying them.
- **Export:** choose File → Export as… (Ctrl+Shift+E), select HTML, PDF or plain text, choose a destination and export. Open file and Show in folder remain beside the completion status. HTML embeds permitted images; PDF preserves document structure on printable pages. Plain text keeps readable text, lists, code indentation and table layout; it cannot contain fonts, colours or images.
- **Print:** choose File → Print preview… (Ctrl+P). Drag the scrollbar to move through the captured document, or use its track, mouse wheel and Page Up/Page Down. Resize the preview as needed. Open printable PDF shows exact page breaks in the installed PDF reader, where Ctrl+P selects the printer and copies.
- **Ask & learn:** ask naturally, for example “I need to add a new AI connection, how can I do that?”. Tern ranks relevant installed guide topics instead of requiring every question word to match. Without a connection, or when a provider fails, an explicitly labelled offline answer appears automatically. No notes or selections are attached. Account access, expired tokens and provider permissions still require the account owner; Tern never changes them automatically.
- **Consistent controls:** dialogs share Paper/Midnight colours, inherited text scaling, wrapping fields, keyboard focus and visible primary actions. Compact screens scroll the form while preserving its action area.
