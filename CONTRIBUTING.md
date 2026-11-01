# Contributing

Use [repository issues](https://github.com/gcfernando/tern-markdown-editor/issues) for reproducible problems and focused suggestions. Check existing reports first. Use [private security reporting](SECURITY.md) for vulnerabilities.

## Before making a change

Read the [project overview](README.md), [documentation index](documentation.md) and [repository standards](REPOSITORY_STANDARDS.md). Keep changes focused and preserve ordinary Markdown files, encoding and line endings, unsaved edits, recovery data, attribution and existing public links.

## Project-specific validation

Develop on Windows with the exact .NET SDK in [global.json](https://github.com/gcfernando/tern-markdown-editor/blob/main/global.json). Follow the [development guide](docs/development.md) for the locked restore, solution build and test commands. UI tests use Avalonia's headless platform; they do not replace physical keyboard, clipboard, printer, screen-reader, DPI or clean-machine checks. Report the Windows version, display scaling and checks actually completed for UI and packaging changes.

Keep application documents and credentials out of tests. Use isolated `TERN_DATA_ROOT` folders and synthetic Markdown fixtures. AI and MCP changes need local protocol fixtures; do not send another person's documents to a provider or commit API keys. Changes to save, overwrite, recovery, export or path handling need regression evidence that unrelated files and unsaved edits remain protected.

## Documentation and repository checks

Use UTF-8, LF line endings and a final newline for repository metadata. Use one level-one title, hierarchical headings, fenced code blocks with a language where useful and blank lines around headings and lists. Preserve two-space Markdown hard breaks and stable anchors. Match relative link paths exactly, including case.

Run `python .github/scripts/check_repository.py` with Python 3.12 or later from the repository root, then run the existing checks relevant to the changed component. Review rendered Markdown and published-page changes. A successful documentation check does not validate application behavior or signing.

## Pull requests

Describe the concrete problem and resulting behavior, affected files, relevant versions and validation. Explain configuration, dependency, compatibility or licensing changes. Include synthetic reproduction steps for failures. Do not include credentials, private documents, machine-specific caches or generated build output.

## Releases and dependency notices

Follow the [release guide](docs/releases.md). Keep application version metadata, release assets, checksums and verification evidence consistent. Only distribute complete packages that passed the required checks. Preserve the [MIT license](LICENSE), [third-party notices](THIRD-PARTY-NOTICES.md) and bundled license texts. Update dependency notices when package locks change; Tern's license does not replace those terms.
