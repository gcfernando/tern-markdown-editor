# Tern 1.0.4

- Unify HTML, PDF and plain-text export under File → Export as… (Ctrl+Shift+E), with explicit output selection and visible completion actions.
- Increase print-preview space and keep scrollbar thumb movement proportional while virtualized blocks are measured.
- Improve Find across Source, Split and Reading, including match highlighting, result navigation and replace review.
- Accept conversational questions about Tern, rank installed guide topics and automatically show offline guide answers when the selected AI provider fails.
- Remove obsolete Copilot CLI switches; retry recognized transport failures once without changing credentials, model or tool permissions.
- Preserve embedded export images, semantic headings, tables and code; correct Windows HTML stylesheet hashes and searchable PDF Unicode mappings.
- Share code syntax colours between preview and export, wrap PDF code by measured glyph width and use installed fallback fonts for code symbols, Japanese text and circled numbers.
- Apply shared dialog themes, wrapping and fixed action areas; refresh Feature Guide and Tutorials, welcome guidance and release documentation.
- Update current application metadata, Windows manifest, About display, MCP metadata and dependency inventory to 1.0.4. Historical release records retain their original versions.


### Version 1.0.4 workflows

- **Find:** use Edit → Find to highlight matches in Source, Split or Reading. Move with Next/Previous or select a result to reveal it. Review replacements before applying them.
- **Export:** choose File → Export as… (Ctrl+Shift+E), select HTML, PDF or plain text, choose a destination and export. Open file and Show in folder appear beside the completion status only for the current complete successful export; editing settings or changing format hides them. HTML embeds permitted images; PDF preserves document structure on printable pages. Plain text keeps readable text, lists, code indentation and table layout; it cannot contain fonts, colours or images.
- **Print:** choose File → Print preview… (Ctrl+P). Drag the scrollbar to move through the captured document, or use its track, mouse wheel and Page Up/Page Down. Resize the preview as needed. Open printable PDF shows exact page breaks in the installed PDF reader, where Ctrl+P selects the printer and copies.
- **Ask & learn:** ask naturally, for example “I need to add a new AI connection, how can I do that?”. Tern ranks relevant installed guide topics instead of requiring every question word to match. Without a connection, or when a provider fails, an explicitly labelled offline answer appears automatically. No notes or selections are attached. Account access, expired tokens and provider permissions still require the account owner; Tern never changes them automatically.
- **Consistent controls:** dialogs share Paper/Midnight colours, inherited text scaling, wrapping fields, keyboard focus and visible primary actions. Compact screens scroll the form while preserving its action area.

Native printer, screen-reader, clipboard, physical-display and clean-machine startup checks remain unverified. Updating source metadata does not publish a release.

### Help, export and guidance improvements

- **Ask & learn:** AI connection and conversation actions sit above the larger question box. Answers render Markdown headings, emphasis, lists, code and tables in one reading area, with the newest answer revealed first. Copy conversation and Start new conversation become available when an answer is visible. What is shared? explains the submitted data. Without a writing connection, Tern answers from installed guides; provider failures also recover to an explicitly labelled offline answer.
- **Export state:** changing Choose a format clears the destination, resets format options and clears completion status and notes. Open file and Show in folder appear only for the current complete successful result. Editing the destination or options, starting another export, or an export failure hides them. Actions share an aligned footer; Export notes start collapsed and only appear when needed.
- **Readable TXT:** paragraphs wrap to a preferred 80-character width. Compact tables stay aligned; wide tables become labelled records. Numbered link references keep long URLs out of prose and retain relative and section destinations. Code indentation, nested lists and Unicode remain intact. Text files cannot display fonts, colours or actual images.
- **PDF badges:** supported inert SVG images use the same bounded native renderer as preview and are embedded as PNG images. Unsupported SVG features still produce an export note instead of silently dropping content. HTML retains permitted SVG images.
- **Next step guidance:** the main window and shared dialog shell show a contextual tip and installed Learn more help. Export tips change for destination selection, progress, success and failure. Ask & learn has its own question and answer guidance.

### Latest screen usability improvements

Ask & learn gives the answer more space once it is ready; the question editor stays available in a compact form for follow-ups. Click a picker result once to choose it, or use Up/Down and Enter. The welcome panel is larger and centered.

In Write with AI, highlight text before opening the panel to use the selection actions. Create content appears only with no selection and Entire note selected. Review the sharing scope and connection, write your instruction, then choose Generate for review. The raw request/JSON section has been removed.

In Review AI suggestion, compare the complete BEFORE and AFTER text. Use Back to improve the instruction, Copy result to copy the full suggestion, Apply changes to accept the proposal, or Close to keep your note unchanged. Insert result preserves the original text and adds the generated content. Applied edits remain unsaved and can be undone. Narrow screens stack the comparison vertically. Disclosure sections use the same themed appearance and keyboard focus cues throughout the app.
