# Review issue matrix

This maintained page restores the review path linked by the original README. It is a current evidence/acceptance matrix, not a reconstructed historical issue log or a claim that all October 30 checks passed.

| Area | Implemented behavior to preserve | Follow-up evidence |
| --- | --- | --- |
| New notes and images | Empty-draft Save; explicit filename priority; Save before insertion then continue | Native picker/cancel/clipboard/drop combinations |
| Save As | In-folder dependency copying, current-byte preservation and collision refusal | Representative portable bundles; unrelated files unchanged |
| Encoding | Original protected bytes; confirmed separate UTF-8 copy | Inspect copies containing replacement characters |
| External changes | Compare/merge and competing-version preservation with explicit failure | Concurrent-editor/filesystem cases |
| Tabs and closing | Ordered session context and Save/Discard/Cancel; closing does not delete | Native focus, group cancellation and reopen scenarios |
| Preview and export | Bounded native rendering and truncation/readiness reporting | External corpus, glyphs, physical printers and output inspection |
| Network features | Guarded image/URL loaders and explicit optional connection requests | Offline package behavior and authorized synthetic live checks |
| Automated gate | Eight external/native cases remain present but excluded from reproducible CI | Execute separately with original inputs and interactive Windows Shell |
| Distribution | Self-contained Windows x64 packaging with checksums | Exact-commit successful run, clean extraction/startup; unsigned status disclosed |

See [validation](../../BEGINNER_REVIEW_VALIDATION.md), [release acceptance](../../V1_RELEASE_LEDGER.md) and the [implementation plan](../IMPLEMENTATION_PLAN.md). Historical changelog entries retain their original context and are not fresh execution evidence.
