# Downloads and releases

Tern's distribution target is **Windows x64**. Application version metadata is **1.0.6**. A preview label, passing subset of checks or release publication does not establish stable native acceptance.

See the [1.0.5 release notes](release-notes-1.0.5.md) and [current workflow guide](workflows-1.0.5.md) for the updated controls and Windows distributions.

## Download and verify

Use [GitHub Releases](https://github.com/gcfernando/tern-markdown-editor/releases). Select a complete release containing `Tern.exe`, `Tern-1.0.5.msi`, `Tern-win-x64.zip` and `SHA256SUMS.txt`, and read its signing and validation notes. The automatically generated source archives are not application packages. If a release has no application assets, do not treat its source ZIP as a substitute.

```powershell
Get-FileHash .\Tern-win-x64.zip -Algorithm SHA256
```

Compare the result with the entry for the same filename in the checksum file from that release. A checksum checks consistency with the supplied manifest; it does not identify a trusted publisher or guarantee security.

Extract the **entire** ZIP to a writable folder and run `Tern.exe`. Keep its runtime, Avalonia/native dependencies, notices, license files and other companion files together. This package is self-contained: it bundles .NET rather than requiring a separately installed runtime. The bundled runtime is pinned to .NET **10.0.15**; package versions such as `Microsoft.Data.Sqlite` **10.0.14** are different dependencies, not the bundled runtime version.

## Signing and Windows warnings

The current packaging route is **unsigned**. No trusted signing identity or completed signing-provider enrollment is established by these documents. Windows can show Unknown publisher or SmartScreen warnings. Do not disable operating-system protections or regard a release tag/checksum as a certificate. Signing, if later configured, cannot guarantee that every Windows reputation warning disappears.

A certificate-based signing route is now available, but it does not create or purchase a publisher identity. Install a trusted code-signing certificate with an accessible private key in the current user's Personal store, and install Windows SDK SignTool. Use the timestamp URL specified by your signing provider:

```powershell
./tools/BuildRelease.ps1 -BuildRoot ../tern-build-output/signed-build -OutputDirectory ../tern-build-output/signed-release -SigningCertificateThumbprint YOUR_CERTIFICATE_THUMBPRINT -TimestampServer https://YOUR_PROVIDER_TIMESTAMP_URL -SignToolPath 'C:/path/to/signtool.exe'
```

The wrapper signs first-party `Tern.exe` and `Tern*.dll` files before packaging. It requires valid, timestamped Authenticode signatures with one signing identity and records binary hashes and publisher evidence in the manifest. `-RequireSigned` requires the fresh publish to carry valid signatures, for example from a custom signing build target. For an externally signed publish, run `python tools/package_release.py --publish SIGNED_FOLDER --output FRESH_OUTPUT_FOLDER --require-signed`. Both routes fail if signature verification is unsuccessful. The extracted-package smoke step verifies signatures again. Unsigned builds retain their explicit unsigned label. The automated workflow remains unsigned until a signing identity is supplied and configured.

Microsoft distinguishes a trusted signature from SmartScreen reputation. Signing establishes publisher identity; new files or publishers can still receive a reputation warning. See [Microsoft's SmartScreen guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation) and [code signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options).

## Local package candidate

Use the pinned SDK and prerequisites in [development](development.md). From the repository root:

```powershell
./tools/BuildRelease.ps1
```

`BuildRelease.ps1` requires fresh build/output directories, prepares a self-contained publish under `../tern-build-output/release-build/publish`, invokes `tools/package_release.py` with explicit publish/output arguments, and runs `tools/TestRelease.ps1` against the extracted package. The helper stages all three downloads and the checksum manifest under `../tern-build-output/release/`:

Before publishing it runs `tools\UpdateDocumentationScreenshots.ps1`: current Release-build synthetic UI captures replace all 20 maintained documentation images. The step rejects failed captures, stale files or screenshot captions that do not match the assembly's informational version, and records image hashes/version under its fresh `artifacts\test-results\docs-capture-*` directory. These are actual headless renders, not native Windows/DPI acceptance. Run that script separately when updating illustrated documentation.

```text
../tern-build-output/release/Tern.exe
../tern-build-output/release/Tern-1.0.6.msi
../tern-build-output/release/Tern-win-x64.zip
../tern-build-output/release/SHA256SUMS.txt
```

Review the helper's validation output and actual build/test reports. The package command does not convert a failed test run into release acceptance. It includes the maintained guides and licensing materials, so missing documentation links must be repaired before packaging.

To repeat the archive's structural/integrity check separately:

```powershell
python ./tools/package_release.py --verify ../tern-build-output/release/Tern-win-x64.zip
```

This checks the archive; it does not rerun the application or test suite.

The existing local-publish script remains separate:

```powershell
./scripts/publish.ps1
# Output: ../tern-build-output/publish/win-x64/Tern.exe and companion files
```

That output is not the final release ZIP. An executable under `src/Tern.Desktop/bin/Release/net10.0/` is also a development output, not a self-contained package guarantee.

## Automated publication

The [release workflow](https://github.com/gcfernando/tern-markdown-editor/actions/workflows/release.yml) restores locked dependencies, builds, runs the reproducible automated gate and packages Windows x64. Successful main-branch publication uses a unique preview identity including the source commit and run. Review the actual workflow and release evidence for a particular candidate; configured automation is not evidence that an uncompleted run succeeded.

The reproducible gate reads six explicit clauses from `tools/automated-test-filter.txt`, excluding eight cases that need untracked original corpus/sample files, rendering inventories, accepted remote-image payloads or an interactive Windows Shell recycle bin. These tests remain available for explicit acceptance when their prerequisites are supplied. General rendering, image and synthetic scrollbar tests remain in CI. Exclusions do not certify unavailable inputs, and the filtered result is not a full-suite pass or native accessibility/printing certification. The [validation guide](BEGINNER_REVIEW_VALIDATION.md#automated-gate-and-external-cases) records the exact boundaries.

## Update, rollback and removal

Close Tern after handling unsaved tabs. Back up notes, attachments and important local state, then extract the new release into a **separate** folder. Do not mix files from different packages. Open synthetic or copied notes first, check saves and exports, and retain the old package until acceptance is complete.

To roll back, close Tern and reopen a retained complete earlier package. State compatibility is not a substitute for a backup. To remove a portable package, close it and remove only its extracted application folder. Notes and the [local data folders](privacy.md#local-data) are separate. Clearing state/recovery can destroy useful recovery information; deleting a profile folder does not automatically remove Windows Credential Manager entries.

## Acceptance before relying on a release

Verify startup from a clean extracted package, local editing and save conflicts, recovery, copied attachments, representative PDF/HTML output and offline local-document use. Check native clipboard/drop, printers, DPI and assistive technology on real Windows systems. Use synthetic text for optional network/provider tests. Record the package hash, source commit, Windows version, scaling and exact checks completed; do not label unperformed checks as passed.

Return to the [README](../README.md) or [documentation index](../documentation.md).

### Help, export and guidance improvements

- **Ask & learn:** AI connection and conversation actions sit above the larger question box. Answers render Markdown headings, emphasis, lists, code and tables in one reading area, with the newest answer revealed first. Copy conversation and Start new conversation become available when an answer is visible. What is shared? explains the submitted data. Without a writing connection, Find guide steps retrieves installed guide instructions; it does not generate a general AI answer. Provider failures also recover to explicitly labelled offline guidance.
- **Export state:** changing Choose a format clears the destination, resets format options and clears completion status and notes. Open file and Show in folder appear only for the current complete successful result. Editing the destination or options, starting another export, or an export failure hides them. Actions share an aligned footer; Export notes start collapsed and only appear when needed.
- **Readable TXT:** paragraphs wrap to a preferred 80-character width. Compact tables stay aligned; wide tables become labelled records. Numbered link references keep long URLs out of prose and retain relative and section destinations. Code indentation, nested lists and Unicode remain intact. Text files cannot display fonts, colours or actual images.
- **PDF badges:** supported inert SVG images use the same bounded native renderer as preview and are embedded as PNG images. Unsupported SVG features still produce an export note instead of silently dropping content. HTML retains permitted SVG images.
- **Next step guidance:** the main window and shared dialog shell show a contextual tip and installed Learn more help. Export tips change for destination selection, progress, success and failure. Ask & learn has its own question and answer guidance.

### Latest screen usability improvements

Ask & learn gives the answer more space once it is ready; the question editor stays available in a compact form for follow-ups. Click a picker result once to choose it, or use Up/Down and Enter. The welcome panel is larger and centered.

In Write with AI, highlight text before opening the panel to use the selection actions. The Create content button and editor toolbar AI flyout have been removed. Other AI commands remain available. Review the sharing scope and connection, write your instruction, then choose Generate for review. The raw request/JSON section has been removed.

In Review AI suggestion, compare the complete BEFORE and AFTER text. Use Back to improve the instruction, Copy result to copy the full suggestion, Apply changes to accept the proposal, or Close to keep your note unchanged. Insert result preserves the original text and adds the generated content. Applied edits remain unsaved and can be undone. Narrow screens stack the comparison vertically. Disclosure sections use the same themed appearance and keyboard focus cues throughout the app.

## Tern 1.0.5 Windows distributions

Build with `tools/BuildRelease.ps1` in a fresh build/output directory. It creates the portable ZIP, standalone `Tern.exe` and `Tern-1.0.5.msi`, hashes every download, checks MSI registry scope and payload coverage, and smokes both executable forms from paths with spaces. `tools/TestMsiPayload.ps1` performs direct cabinet extraction and compares all payload hashes without installing associations.

The MSI build uses the pinned WiX 3.14.1 binaries from the official WiX release; `tools/GetWix.ps1` verifies their SHA-256. The compiler and linker are build-only dependencies. The MSI embeds unmodified WixUI and WixUtil helpers; their license and exact source references are included under `licenses/wix/3.14.1`. Windows Installer ICE validation requires a normal Windows account with access to the Installer service. The wizard presents Welcome, the MIT license, installation folder, optional Start menu/desktop shortcuts, confirmation, progress and a successful Finish page with optional Launch Tern. It registers the Tern icon for Installed apps. The per-user MSI intentionally produces ICE91 notices; it does not offer a machine-wide install. The language-neutral SQLite binary is declared with neutral language metadata. An additional ICE61 notice records the intentional ability to replace the earlier same-version 1.0.5 candidate; removal occurs inside the installer transaction for rollback. Full validation output is retained with the candidate.

Standalone publishing uses the .NET single-file bundle with native-library extraction, without trimming. Installed help is compiled in; verbatim license notices and illustrated guides are embedded and available through About → Guides and licenses. The EXE inside the portable ZIP depends on its adjacent files.

A timestamped Authenticode certificate and Windows signing tools are required for a trusted signed release. Without signing credentials the artifacts remain unsigned candidates. Do not describe checksum verification as publisher trust. `-RequireSigned` refuses a build without signing credentials.

MSI registration and Settings → Windows integration register only Markdown support, Open with, and the classic Open with Tern verb for one .md file. No UserChoice or .md default value is written. Windows 11 classic verbs may appear under Show more options; see [Microsoft context-menu guidance](https://blogs.windows.com/windowsdeveloper/2021/07/19/extending-the-context-menu-and-share-dialog-in-windows-11/). Native installation, uninstallation, Explorer right-click and user-selected default double-click opening remain separate acceptance checks; run them in an isolated Windows user account to preserve the current user’s default.
