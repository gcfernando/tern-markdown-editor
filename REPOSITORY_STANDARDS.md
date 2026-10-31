# Repository standards

This repository follows the shared administration and documentation baseline across Gehan Fernando's repositories. Tern-specific build, document-safety, packaging and licensing requirements remain in its guides.

## Structure

| Location | Purpose |
| --- | --- |
| `README.md` | Project overview, prerequisites, downloads, usage and support |
| `documentation.md` | Entry point to the maintained guides |
| `CONTRIBUTING.md` | Contribution process and validation commands |
| `SECURITY.md` | Private vulnerability reporting and scope |
| `LICENSE` | MIT terms for Tern's own code |
| `THIRD-PARTY-NOTICES.md` and `licenses/` | Dependency attribution and license texts |
| `.github/ISSUE_TEMPLATE/` | Public issue forms and private security-reporting link |
| `.github/pull_request_template.md` | Change description, validation and impact |
| `.github/workflows/` | Repository checks and verified application publication |
| `.github/scripts/check_repository.py` | Dependency-free metadata check |
| `src/`, `tests/`, `scripts/`, `tools/` | Application, tests and build helpers |
| `docs/` and `Images/` | Written guides and supplied interface screenshots |

Preserve source paths, the root solution, package boundaries, test fixtures and existing image filenames. Keep new guides with the existing documentation, use lowercase hyphenated ordinary filenames and conventional root names such as `README.md`. Generated output belongs in ignored artifact directories.

## Formatting and configuration

Use UTF-8 without a byte order mark, LF line endings and a final newline for root documentation and repository administration. Use one level-one title in each primary guide, hierarchical headings and blank lines around headings and lists. Keep intentional two-space Markdown line breaks, code fences, attribution and stable anchors. Match link paths and filename case exactly.

Shared `.editorconfig` and `.gitattributes` sections cover documentation and administration. Existing source-specific settings take precedence within their scope. Do not run repository-wide source formatters or Git renormalization as part of documentation maintenance. Do not rewrite imported specification fixtures or third-party license texts to match prose style rules.

## Automation

Active workflows pin external actions to full commit SHAs, declare read-only default contents permissions and set job timeouts. Grant release-write permissions only to publication jobs. Dependabot proposes GitHub Actions updates for review; preserve pinned SDKs and dependency locks. The repository check runs on pushes, pull requests and manual dispatch.

Run `python .github/scripts/check_repository.py` with Python 3.12 or later. Its scope is repository documentation and administration. Follow [CONTRIBUTING.md](CONTRIBUTING.md) for application checks. Release evidence must describe the actual source, platform, tests, package and limitations rather than implying complete acceptance from a badge.

## GitHub settings and Pages

The shared baseline uses `main`, an enabled issue tracker, the documentation homepage, all three merge methods, read-only default workflow tokens, private vulnerability reporting, secret scanning and push protection. Main-branch protection prevents force pushes and deletion. Review and required-check rules should reflect the project's contribution process.

GitHub Pages publishes from `main` at the root, using `_config.yml`, `_layouts/` and `assets/`. The documentation excludes application source, tests and generated output. Keep the homepage consistent with `https://gcfernando.github.io/tern-markdown-editor/`. Test published links and anchors after deployment, including the profile catalog entry.

## Licensing and review boundaries

Tern's own code uses the [MIT License](LICENSE). Dependencies, fonts and native/runtime components retain their own terms and notices. Preserve those notices in source and downloadable packages. A metadata check does not validate application security, dependency-license completeness, Windows publisher reputation or every desktop environment.
