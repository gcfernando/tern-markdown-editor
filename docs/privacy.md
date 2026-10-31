# Privacy and network access

Local Markdown editing does not require an account, activation service or telemetry account. Tern nevertheless has specific network-capable features: remote images, explicit URL import, optional AI/MCP requests and an optional installed Copilot CLI. This page describes implementation boundaries, not a completed independent privacy/security audit. Use [SECURITY.md](../SECURITY.md) for sensitive reports.

## Local data

Your notes remain ordinary files at the locations you choose. Attachments and linked files may live beside them or elsewhere. Saved-text history, workspace/session state, recovery checkpoints and indexes can contain document text, paths or writing context; treat backups and shared machine accounts accordingly.

| Data | Default location |
| --- | --- |
| Settings and connection metadata | `%APPDATA%\Tern` |
| State | `%LOCALAPPDATA%\Tern` |
| Recovery | `%LOCALAPPDATA%\Tern\recovery` |
| Search indexes | `%LOCALAPPDATA%\Tern\index` |
| Saved AI credentials | Windows Credential Manager |

`TERN_DATA_ROOT` redirects file-based state to separate `settings`, `state`, `recovery` and `index` directories. It does not relocate Windows Credential Manager. Back up notes and important state before cleanup; clearing recovery can remove the only recoverable copy of unsaved work. Removing an application or profile directory does not automatically remove saved credentials.

## Remote images

**Opening or previewing a local note containing public remote images can contact those image hosts without a separate prompt for each image.** Resource preparation for export can also request them. Hosts can observe normal request metadata, including the requested URL and source network address. A URL can itself contain sensitive information.

The setting controlling external-link opening governs clicked links, not image downloads. For network-free use, choose local-only documents/assets and test the actual package in the intended offline environment. The current implementation does not establish installed offline acceptance for every input.

The live image loader is bounded: public HTTP/HTTPS destinations, guarded DNS/redirect handling, no supplied authentication/cookies/proxy, at most three redirects, 15-second timeout, four concurrent requests, byte/pixel bounds and format validation. It rejects private/loopback destinations for this path. SVG has stricter limits and active/external content restrictions. Live image bytes are not a persistent offline cache; URL-import bundles below are different. These guards do not make untrusted remote content universally safe.

## Explicit URL import

Open from URL fetches a reviewed Markdown-file URL. Public GitHub file links can become raw-content links. Explicit localhost URLs are supported for this import path, unlike the remote image loader. Public destinations cannot redirect into private/loopback networks; credential-bearing URLs and unsafe redirects are rejected. The loader uses bounded requests without browser cookies or automatic account login.

The Markdown limit is 10,000,000 bytes. Supported directly referenced assets can be cached in a bundle bounded at 128 assets/100,000,000 bytes total. Complete bundles reopen offline; partial bundles report missing assets and can retry. Editing the imported draft does not upload to the server. See [URL import usage](user-guide.md#open-from-url).

## Optional AI connections

Settings → AI connections configures chat-completions-compatible, Ollama native-chat or Claude Messages protocols. Endpoints require HTTPS, with HTTP allowed for loopback development services. A familiar model name does not establish that the endpoint is genuine, compatible or included in your subscription. Live provider/model compatibility remains separately unverified.

Connection metadata is file-based; saved keys use Windows Credential Manager under per-connection `Tern.AI/…` targets. Unsaved keys remain in memory. Do not paste real keys into Markdown, screenshots, test fixtures, issues or logs. Provider authentication and retention policies remain those of the selected service.

Reloading saved profiles is not itself a generation request. Explicit connection checks/model discovery can contact the endpoint. Before generation, review the selected connection and model and the text to be transmitted:

- **Generate and update note** can send the complete active Markdown plus your question. Its response becomes one undoable edit; it does not automatically save the note.
- **Guide questions** send the question without automatically attaching the active note.
- Cancellation cannot recall text already transmitted. Undo can revert a local edit, not a provider's receipt or retention of data.

The adapters use bounded, non-streaming requests and response limits. Local protocol fixtures validate request/response contracts; they are not live-service certification or proof of content quality. Review generated text before saving or executing anything it suggests.

## Manual MCP tools

The implementation targets MCP **2025-11-25 Streamable HTTP**, with initialization, tool listing and manually requested tool calls. It does not claim newer-protocol compatibility, stdio support, OAuth or autonomous tool execution.

Review the exact endpoint, selected discovered tool and JSON arguments before calling it. Optional bearer credentials, bounded responses and local request validation do not neutralize a tool's authority. A tool can change an external system; Tern's Undo cannot reverse that. Do not send private note contents as tool arguments unless authorized. HTTPS is required except explicit loopback HTTP development endpoints.

## Optional Copilot CLI

The optional integration invokes a deliberately selected installed `copilot.exe`; it is not a bundled service or entitlement. It uses a bounded reviewed prompt with a separate temporary working/configuration context, and requests that tools, MCP, hooks, updates and instruction discovery be disabled. It does not grant the CLI automatic workspace context. Tokens are passed to the child process environment rather than written to a Tern token file.

The implementation's expected CLI contract still needs live compatibility verification. External CLI behavior and provider data handling are outside Tern's sole control. Keep it disabled when that boundary is unsuitable for your documents.

## Active content and external links

Preview is native rather than a browser web view. HTML/SVG processing restricts active content; Markdown scripts are not executed as web-page scripts. This is not a guarantee that every hostile input is harmless. Clicking a permitted external link can launch another application/browser, whose policies apply independently.

Use synthetic inputs for testing network behavior and remove secrets, personal paths and document text from shared diagnostics. The [release ledger](V1_RELEASE_LEDGER.md) separates static/source checks from runtime offline and native acceptance.
