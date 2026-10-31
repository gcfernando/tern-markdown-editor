# Security policy

## Reporting a vulnerability

Use [GitHub private vulnerability reporting](https://github.com/gcfernando/tern-markdown-editor/security/advisories/new) for sensitive findings. Do not disclose credentials, private documents, confidential source or exploitable details in public issues or pull requests.

Include the affected version or commit, component, impact and a minimal reproduction with synthetic files. Relevant components include Markdown/HTML/SVG rendering, local and remote resource handling, workspace containment, save/recovery/export operations, persistent state, and optional AI/MCP connections. Remove personal paths, tokens, document contents and other private information from logs and screenshots. Use [repository issues](https://github.com/gcfernando/tern-markdown-editor/issues) for ordinary bugs and documentation corrections.

## Scope and maintenance

The [README](README.md) and [user guide](docs/user-guide.md) describe supported platforms, implemented features and known limitations. Current distribution targets Windows x64. This policy does not establish an additional support lifetime, response deadline, completed security audit or guarantee that every input is safe.

Tern processes ordinary local files and can access the network for explicitly used URL imports, displayed remote images and optional AI/MCP connections. Review destinations and selected text before transmitting data. Keep real secrets in the supported credential store rather than in documents, configuration examples or source control. See the [privacy and network guide](docs/privacy.md).

## Downloads and verification

Download complete packages from [GitHub Releases](https://github.com/gcfernando/tern-markdown-editor/releases) and compare their SHA-256 checksums with the manifest from the same release. Release notes identify signing status. Checksums detect changed downloads relative to the supplied manifest; they do not establish a trusted Windows publisher or guarantee application security. Do not disable operating-system protections to make verification pass.
