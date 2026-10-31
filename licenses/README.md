# Third-party license files

This directory retains license texts and notices for Tern's locked desktop dependency graph. The graph includes build-only and non-Windows package entries; its presence here does not imply that every platform's native assets are distributed in the Windows ZIP.

## Contents

- `dependency-inventory.json`: exact package versions, package license declarations, repository metadata and hashes of copied notices.
- `packages/`: unmodified license and notice files supplied by the resolved NuGet packages.
- `expressions/`: full SPDX texts for declared MIT, BSD-2-Clause and Apache-2.0 expressions; package-specific attribution is recorded in the root notices and inventory.
- `upstream/`: Avalonia's license and third-party notice, Markdig's BSD license, and the Inter font's SIL Open Font License.
- `dotnet-runtime/`: the self-contained .NET runtime's exact license and third-party notice.

## Maintenance

Review these files when changing dependency locks or the bundled runtime. Preserve upstream text, copyright notices and reserved-font-name terms. The MIT license for Tern's own code does not relicense these components. Package metadata is an inventory aid rather than a claim that a legal audit was completed.

See [the source notices](https://github.com/gcfernando/tern-markdown-editor/blob/main/THIRD-PARTY-NOTICES.md) for attribution and source links. These files are bundled with downloadable application packages and retained in GitHub source; the documentation site links to that source directory rather than publishing duplicate license pages.
