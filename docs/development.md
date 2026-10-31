# Development

Develop Tern on Windows using PowerShell and the exact .NET SDK in [global.json](https://github.com/gcfernando/tern-markdown-editor/blob/main/global.json): **10.0.402**, with SDK roll-forward disabled. The application version in `Directory.Build.props` remains **1.0.1**. Current distribution targets Windows x64.

## Restore, build and run

From the repository root:

```powershell
dotnet --version
dotnet restore Tern.slnx --locked-mode
dotnet build Tern.slnx -c Release --no-restore -m:1
dotnet run --project src/Tern.Desktop -c Release --no-build
```

`Directory.Packages.props` and project lockfiles pin dependencies. Nullable analysis, deterministic builds and warnings-as-errors are enabled. Current core packages include Avalonia 12.1.4, AvaloniaEdit 12.0.0, Markdig 1.5.0, SQLite bindings 10.0.14 and PDFsharp 6.2.4; use the manifests as the source of truth when changing them.

Use isolated synthetic data during development:

```powershell
$env:TERN_DATA_ROOT = Join-Path $PWD 'artifacts/local-profile'
dotnet run --project src/Tern.Desktop -c Release --no-build
Remove-Item Env:TERN_DATA_ROOT
```

The override affects file-based state, not Windows Credential Manager. Do not use real API keys or private documents in fixtures. Native GUI checks should not share a profile or interfere with an actively used application.

## Tests and evidence

The complete unfiltered test command is:

```powershell
dotnet test Tern.slnx -c Release --no-build --no-restore -m:1
```

Eight test projects cover unit, application, rendering, export, workspace, preview-view, desktop and security behavior. `tests/Tern.Benchmarks` is a separate console, not a ninth test suite. UI checks use Avalonia's headless platform; protocol checks use local fixtures, not live provider credentials.

Some tests depend on supplied files outside the tracked checkout or interactive Windows Shell behavior. CI's reproducible gate uses `tools/automated-test-filter.txt` to exclude **eight** such cases; run the unfiltered command only with the required inputs/environment when expecting those cases to execute. The [validation guide](BEGINNER_REVIEW_VALIDATION.md#automated-gate-and-external-cases) identifies them. The original tests and assertions remain present. Do not call the filtered result a full-suite pass or native accessibility/printing certification.

For a change, run the relevant regression tests and inspect results. Save, overwrite, recovery, resource, path and export changes need evidence protecting unrelated files and unsaved text. AI/MCP changes should use loopback fixtures with synthetic payloads. Record actual commit, environment, counts, skipped/excluded cases and report location; do not reuse historical changelog totals as a new result.

## Architecture

| Path | Responsibility |
| --- | --- |
| `src/Tern.Domain` | Document model, text formats, diff and three-way merge |
| `src/Tern.Filesystem` | Load/save, encoding preservation and change monitoring |
| `src/Tern.Markdown` | Markdig parsing, analysis, formatting and structured edits |
| `src/Tern.Rendering` | Bounded preview tree, sanitizer, resources, math and Mermaid |
| `src/Tern.PreviewView` | Avalonia native preview control |
| `src/Tern.Export` | HTML, PDF and text output |
| `src/Tern.Workspaces` | Scanning, watchers, link graph, file operations and stores |
| `src/Tern.Search` | SQLite full-text index, queries, find/replace and Quick Open |
| `src/Tern.Recovery` | Recovery checkpoints |
| `src/Tern.App` | UI-independent view models, commands and settings |
| `src/Tern.Desktop` | Avalonia desktop application; executable assembly `Tern` |
| `tests` | Automated test projects, fixtures and benchmark console |

Network-capable paths are narrowly scoped to remote images, URL import and explicit connections; review [privacy](privacy.md) before changing them. A static offline assembly audit is not a runtime proof that no request can occur.

## Packaging

```powershell
./tools/BuildRelease.ps1
```

The wrapper publishes to `artifacts/release-build/publish`, invokes `tools/package_release.py` with explicit publish/output arguments, and runs the extracted-package smoke helper. Final `Tern-win-x64.zip` and `SHA256SUMS.txt` go to `artifacts/release/`. It refuses existing build/output directories to preserve prior evidence; choose fresh `-BuildRoot` and `-OutputDirectory` values for another candidate. The existing `./scripts/publish.ps1` instead produces `artifacts/publish/win-x64`. Read the [release guide](releases.md) for automation, signing and acceptance gates.

## Benchmarks

```powershell
dotnet run --project tests/Tern.Benchmarks -c Release -- --quick
dotnet run --project tests/Tern.Benchmarks -c Release
```

Results are written to `artifacts/benchmarks/`. Record the input, hardware, build and warm/cold conditions. No timing or responsiveness result is established by these instructions; see [performance scope](PERFORMANCE.md).

## Offline package-cache checks

Vulnerability-feed failures can produce `NU1900`, which fails the normal warnings-as-errors build. For an explicitly offline local check using already cached packages, `-p:NuGetAudit=false` can be passed to that command only. This skips dependency vulnerability auditing for that run and is not a release audit. Do not alter project defaults or weaken release checks to hide a restore failure.

## Documentation checks

Use Python 3.12 or later for the repository checker:

```powershell
python .github/scripts/check_repository.py
```

Review rendered Markdown/Pages, exact-case relative links and screenshots as well as syntax. Test-fixture Markdown is application input, not product documentation to rewrite for style. Follow [CONTRIBUTING.md](../CONTRIBUTING.md) for focused changes and evidence.
