# Performance fixes and Windows publisher warning

Review date: 5 November 2026. Performance fixes are included in application version 1.0.3. Existing unrelated work was preserved. At review time no release had been published. No Windows security setting was changed.

## Changes against the performance review

| Area | Before | After | Verification |
| --- | --- | --- | --- |
| Workspace preparation | The session and indexer each enumerated the tree | Indexing accepts the session's fresh snapshot; file stamps are rechecked and watcher events buffer during preparation | Shared-snapshot regression test and workspace tests |
| Ordinary saves and watcher hints | Full tree scans followed existing-file saves and change batches | Metadata updates preserve tree topology; atomic delete/create hints are rechecked; pending events coalesce | Existing-file and atomic-save tree-identity tests |
| Typing and workspace links | Synchronous document analysis and graph resolution in the editing path | Latest-version graph updates run after an idle interval in one background worker; stale updates cannot overwrite saved/newer text | Latest-revision, duplicate-tab and unsaved-deletion regressions |
| Source highlighting | Rebuilt code-fence state across all lines after an edit | Reuses line states until a change propagates; an ordinary edit in the 100,000-line fixture visits at most two states | Highlighting regression, including fence insertion |
| Oversized file changes | Watcher and text-provider reads could bypass scan admission | Text provider, watcher and indexer recheck size before allocation; oversized entries are excluded | Bounded-read and watcher regressions |
| Analysis and preview | Obsolete parses could overlap; Source view still scheduled previews | Document analysis is serialized; obsolete preview model work is cancelled and Source view defers preview | Application and desktop regression suites |
| Saved history | Every capture loaded and verified all retained versions | Capture checks the newest version for deduplication on a background worker; browsing still verifies opened versions | History integrity, retention and save tests |
| Merge memory | Diff trace could approach 122 MiB per invocation at the old edit-distance ceiling | Trace-array payload is capped at 8 MiB, with a conservative broader-change fallback; merge computation runs in the background | Large unrelated-input test asserts less than 9 MiB allocation for the diff call |
| URL dependencies | Sequential fetches with separate timeouts | At most four transfers, aggregate admission and a two-minute whole-operation deadline | Overlap/concurrency, cancellation, redirect and portability tests |
| Online asset memory | Open tabs retained complete compressed asset dictionaries | Tabs use immutable cache files; metadata-only cache reads avoid eager asset buffers | Offline reopen and portable-copy tests assert disk resources and empty retained dictionaries |
| Cache retention | Superseded snapshots remained until reset | Startup expires superseded snapshots older than seven days; cross-process file leases protect open snapshots | Leased snapshot survives pruning; unleased stale snapshot expires |
| Search input | Synchronous picker queries and superseded Find work | Large/dirty queries run in the background; Find cancels superseded requests; small clean pickers remain immediate | Search feedback, picker, navigation and Find tests |
| Clipboard and recovery | PNG encoding and recovery discovery could run on the UI thread | Both run on background workers | Image-command and recovery workflows |
| AI overhead | Every generation repeated its preflight | Successful exact-configuration checks can be reused for up to 30 seconds; credential changes/failures invalidate them; transport pooling retains request-scoped authorization | Probe-count, credential-change and existing AI validation tests |

Source files are still ordinary Markdown. Existing atomic-save, conflict, revision and credential protections remain in place. Feature Guide and Tutorials, the user guide, privacy guide, performance notes and release-signing instructions were updated.

## Windows warning

The screenshot shows SmartScreen reporting an unrecognized application and **Unknown publisher**. A source-code change cannot create a trusted publisher identity. The user confirmed that no signing identity is available yet.

The release tooling now supports a certificate in the current user's Personal store, SHA-256 signing and a provider timestamp. It verifies every first-party binary with Windows Authenticode, requires one publisher identity, records hashes and publisher evidence, and verifies extracted signed packages again. A required-signature build fails instead of silently producing an unsigned package. Unsigned builds keep their explicit unsigned label.

Observed verification: the signature verifier rejected the current unsigned binaries with `NotSigned`. Signed-manifest and fail-closed packaging paths were tested with synthetic fixtures; no real trusted certificate was used and no signed release was produced. This work does not modify an already downloaded executable; the current build remains unsigned.

To finish the publisher fix, obtain a trusted code-signing identity and use the [signed release instructions](releases.md#signing-and-windows-warnings). Microsoft explains that a trusted signature identifies the publisher but does not guarantee immediate SmartScreen reputation. See [SmartScreen reputation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation) and [code signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options).

## Measurement scope and remaining limits

The benchmark comparison uses the same Release `--quick` harness, synthetic inputs and machine as the earlier report. Workspace scan/index measurements have one sample each. Small warm-run timing differences are not evidence of a universal speedup. Component timings exclude native input-to-paint latency and installed startup.

Markdig parsing itself remains non-interruptible. Source highlighting omits cosmetic regex passes on lines longer than 32,768 characters. Memory-bounded merges can present broader conflicts. Current online snapshots are retained, so cache cleanup is not a global disk quota. The 64 MiB ordinary-file ceiling remains a safety boundary; 1 GB editing is unsupported.

No total-process memory reduction, absence of long-session leaks, native cold-start improvement or live AI/network latency improvement is claimed from these checks. Those require separate interactive profiling and controlled network measurements.

## Measured before and after

Windows build 10.0.26200, 20 logical processors, .NET 10.0.14, Release quick harness. No tests ran concurrently with the after measurement. The table reports medians; rows with one run are single observations. Negative time changes mean less elapsed time.

| Component | Before | After | Change | Unit | Runs before/after |
| --- | ---: | ---: | ---: | --- | ---: |
| open 10 MB / 100k lines (read+decode+hash) | 26.06 | 25.29 | -2.9% | ms | 3/3 |
| open 100 KB GFM | 0.76 | 0.69 | -10.3% | ms | 5/5 |
| save 100 KB (atomic, verified against baseline) | 19.87 | 17.74 | -10.7% | ms | 5/5 |
| save 10 MB (atomic, verified against baseline) | 23.30 | 26.27 | +12.8% | ms | 3/3 |
| editor document model: create from 10 MB | 132.56 | 133.46 | +0.7% | ms | 3/3 |
| editor document model: 100 random single-char inserts | 0.61 | 0.59 | -3.9% | ms | 3/3 |
| editor document model: 1,000 offset->line lookups | 1.35 | 1.22 | -9.7% | ms | 3/3 |
| find next in 10 MB (ordinal, no match, worst case) | 2.14 | 1.09 | -48.9% | ms | 3/3 |
| document.Text materialisation (10 MB) | 0.00 | 0.00 | -50.0% | ms | 3/3 |
| preview build 100 KB GFM (parse + node tree) | 35.56 | 36.57 | +2.8% | ms | 5/5 |
| preview build 1 MB mixed GFM | 242.89 | 220.28 | -9.3% | ms | 3/3 |
| preview build 10 MB (limits apply) | 885.06 | 785.33 | -11.3% | ms | 2/2 |
| HTML export 100 KB | 14.78 | 18.17 | +22.9% | ms | 3/3 |
| HTML export 1 MB | 243.61 | 286.94 | +17.8% | ms | 3/3 |
| plain-text export 1 MB | 267.33 | 282.56 | +5.7% | ms | 3/3 |
| PDF export 100 KB | 138.12 | 180.72 | +30.8% | ms | 2/2 |
| workspace scan 2,000 notes | 6086.37 | 2362.94 | -61.2% | ms | 1/1 |
| workspace index 2,000 notes (initial, full) | 7746.18 | 3399.86 | -56.1% | ms | 1/1 |
| workspace re-index 2,000 notes (nothing changed) | 5572.66 | 2473.49 | -55.6% | ms | 1/1 |
| search first query after index (2,000 notes) | 29.91 | 31.03 | +3.7% | ms | 1/1 |
| search query latency, 2,000 notes, 20 results | 2.74 | 3.56 | +29.6% | ms | 30/30 |
| link graph build 2,000 notes | 1242.35 | 975.74 | -21.5% | ms | 1/1 |
| workspace scan 10,000 notes | 21697.94 | 8541.12 | -60.6% | ms | 1/1 |
| workspace index 10,000 notes (initial, full) | 30838.09 | 13903.81 | -54.9% | ms | 1/1 |
| workspace re-index 10,000 notes (nothing changed) | 26498.57 | 10490.22 | -60.4% | ms | 1/1 |
| search first query after index (10,000 notes) | 13.34 | 12.26 | -8.1% | ms | 1/1 |
| search query latency, 10,000 notes, 20 results | 10.16 | 10.37 | +2.1% | ms | 30/30 |
| link graph build 10,000 notes | 6226.94 | 4408.10 | -29.2% | ms | 1/1 |
| recovery checkpoint write 100 KB (flush to disk) | 8.34 | 9.23 | +10.7% | ms | 5/5 |
| byte-preserving no-op save (fixtures identical) | 9.00 | 9.00 | unchanged | count | 1/1 |

The 10,000-note scan fell from 21.70 s to 8.54 s (60.6% less elapsed time), initial indexing including its scan from 30.84 s to 13.90 s (54.9% less), and unchanged re-indexing from 26.50 s to 10.49 s (60.4% less). Shared-snapshot startup additionally avoids a duplicate tree enumeration, which this standalone indexer benchmark does not measure.

The results are mixed outside the targeted scan/index paths. Warm 10,000-note search was approximately unchanged (10.16 to 10.37 ms median); some export timings increased, including the two-run PDF result (138.12 to 180.72 ms). Those paths were not optimized here, and the small sample counts do not establish a general export improvement or a statistically confirmed regression. Nine of nine byte-preserving save fixtures passed both runs.

## Validation

- Complete repeatable eight-project gate: 2,888 passed, zero failed, one existing fixture-dependent test skipped. The repository external/manual acceptance filter was retained.
- Final watcher refinement checks: 341 application tests and 34 affected desktop/workspace tests passed, including the added atomic-save regression.
- Release-policy and packaging tests: 27 passed. Signed-package fixture tests use synthetic evidence, not a real certificate.
- All four release/signing PowerShell scripts parsed successfully. The signature verifier rejected unsigned binaries with NotSigned as intended.
- Repository policy and whitespace checks passed. No release was published.

Raw development-workspace evidence is retained under `artifacts/benchmarks/performance-before.*`, `performance-after.*`, `performance-comparison.json`, and `artifacts/test-results/performance-passed` plus `performance-watcher-final`. Generated artifacts are not shipped as documentation dependencies.
