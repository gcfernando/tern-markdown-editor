# Downloads and releases

Tern's distribution target is **Windows x64**. Application version metadata remains **1.0.1**. A preview label, passing subset of checks or release publication does not establish stable native acceptance.

## Download and verify

Use [GitHub Releases](https://github.com/gcfernando/tern-markdown-editor/releases). Select a complete release containing `Tern-win-x64.zip` and `SHA256SUMS.txt`, and read its signing and validation notes. The automatically generated source archives are not application packages. If a release has no application assets, do not treat its source ZIP as a substitute.

```powershell
Get-FileHash .\Tern-win-x64.zip -Algorithm SHA256
```

Compare the result with the entry for the same filename in the checksum file from that release. A checksum checks consistency with the supplied manifest; it does not identify a trusted publisher or guarantee security.

Extract the **entire** ZIP to a writable folder and run `Tern.exe`. Keep its runtime, Avalonia/native dependencies, notices, license files and other companion files together. This package is self-contained: it bundles .NET rather than requiring a separately installed runtime. The current runtime is .NET **10.0.13**; package versions such as `Microsoft.Data.Sqlite` **10.0.14** are different dependencies, not the bundled runtime version.

## Signing and Windows warnings

The current packaging route is **unsigned**. No trusted signing identity or completed signing-provider enrollment is established by these documents. Windows can show Unknown publisher or SmartScreen warnings. Do not disable operating-system protections or regard a release tag/checksum as a certificate. Signing, if later configured, cannot guarantee that every Windows reputation warning disappears.

## Local package candidate

Use the pinned SDK and prerequisites in [development](development.md). From the repository root:

```powershell
./tools/BuildRelease.ps1
```

`BuildRelease.ps1` requires fresh build/output directories, prepares a self-contained publish under `artifacts/release-build/publish`, invokes `tools/package_release.py` with explicit publish/output arguments, and runs `tools/TestRelease.ps1` against the extracted package. The helper stages the final ZIP and checksum manifest under `artifacts/release/`:

```text
artifacts/release/Tern-win-x64.zip
artifacts/release/SHA256SUMS.txt
```

Review the helper's validation output and actual build/test reports. The package command does not convert a failed test run into release acceptance. It includes the maintained guides and licensing materials, so missing documentation links must be repaired before packaging.

To repeat the archive's structural/integrity check separately:

```powershell
python ./tools/package_release.py --verify artifacts/release/Tern-win-x64.zip
```

This checks the archive; it does not rerun the application or test suite.

The existing local-publish script remains separate:

```powershell
./scripts/publish.ps1
# Output: artifacts/publish/win-x64/Tern.exe and companion files
```

That output is not the final release ZIP. An executable under `src/Tern.Desktop/bin/Release/net10.0/` is also a development output, not a self-contained package guarantee.

## Automated publication

The [release workflow](https://github.com/gcfernando/tern-markdown-editor/actions/workflows/release.yml) restores locked dependencies, builds, runs the reproducible automated gate and packages Windows x64. Successful main-branch publication uses a unique preview identity including the source commit and run. Review the actual workflow and release evidence for a particular candidate; configured automation is not evidence that an uncompleted run succeeded.

The gate intentionally excludes **eight external-input or interactive-native cases**, recorded in [the validation guide](BEGINNER_REVIEW_VALIDATION.md#automated-gate-and-external-cases) and `tools/automated-test-filter.txt`. Tests remain present; passing the gate does not claim the excluded cases passed. See the [acceptance ledger](V1_RELEASE_LEDGER.md) for required native follow-up.

## Update, rollback and removal

Close Tern after handling unsaved tabs. Back up notes, attachments and important local state, then extract the new release into a **separate** folder. Do not mix files from different packages. Open synthetic or copied notes first, check saves and exports, and retain the old package until acceptance is complete.

To roll back, close Tern and reopen a retained complete earlier package. State compatibility is not a substitute for a backup. To remove a portable package, close it and remove only its extracted application folder. Notes and the [local data folders](privacy.md#local-data) are separate. Clearing state/recovery can destroy useful recovery information; deleting a profile folder does not automatically remove Windows Credential Manager entries.

## Acceptance before relying on a release

Verify startup from a clean extracted package, local editing and save conflicts, recovery, copied attachments, representative PDF/HTML output and offline local-document use. Check native clipboard/drop, printers, DPI and assistive technology on real Windows systems. Use synthetic text for optional network/provider tests. Record the package hash, source commit, Windows version, scaling and exact checks completed; do not label unperformed checks as passed.

Return to the [README](../README.md) or [documentation index](../documentation.md).
