# Performance scope

Tern eagerly loads documents up to a 64 MiB safety ceiling. This is not a responsiveness guarantee, and **1 GB editing is unsupported**. Preview has smaller source/node/depth/time budgets; workspace scanning has a separate default per-file ceiling. See [known limits](user-guide.md#known-limits).

## Run benchmarks

From the repository root with the pinned SDK:

```powershell
dotnet run --project tests/Tern.Benchmarks -c Release -- --quick
dotnet run --project tests/Tern.Benchmarks -c Release
```

The benchmark console writes results under `artifacts/benchmarks/`. Record commit, SDK/runtime, CPU, RAM, storage, Windows version, input composition and size, repetitions and cold/warm conditions. Do not compare unlike input sizes or a source build with a different packaged configuration without qualification.

## Interpret results

This page supplies reproducible commands, **not current measured timings**. Historical changelog references to startup proxies or earlier test counts do not establish performance for this snapshot. A parser/renderer microbenchmark does not establish editor typing latency, scrolling, total process memory, live resource latency or installed startup behavior.

Native acceptance should include representative local notes, tables, diagrams, images and large documents below the supported ceiling, together with warnings/truncation behavior. Use local synthetic assets to separate document work from network latency. Preserve raw output when reporting a regression. See the [release ledger](V1_RELEASE_LEDGER.md).

## November 2026 changes

Workspace opening shares a fresh scan with indexing. Existing-file saves and ordinary watcher changes preserve tree topology, while membership changes and watcher overflow still rescan. Pending watcher events coalesce; unsaved link-graph work runs in a debounced background queue. File reads recheck their size before allocation, and physical-path validation still checks reparse points.

Source highlighting reuses line states until a changed fence/front-matter state propagates. Document analysis serializes parser work; preview builds cancel obsolete model construction and are deferred in Source view. The underlying Markdig parse remains non-interruptible. Large merges use an 8 MiB trace budget and fall back to broader changed regions conservatively.

History deduplication reads the newest saved version; browsing verifies all opened versions. Large or dirty workspace searches run in the background, Find cancels superseded work, and small clean pickers remain immediate. Clipboard PNG encoding and recovery discovery run off the UI thread.

URL import uses at most four dependency transfers, aggregate bundle admission, and a two-minute operation deadline. Online tabs use disk-backed immutable assets instead of retaining all compressed asset buffers. Startup cleanup expires superseded snapshots older than seven days while file leases protect open snapshots across processes. Current snapshots are retained; this is not a global cache disk quota.

Writing requests can reuse successful checks for up to 30 seconds with the exact configuration and a credential fingerprint. Connection changes and failures require another check. HTTP transport pooling reuses connections without shared authorization headers or cookies. Responses remain bounded and fully reviewed before applying; live provider latency has not been benchmarked.
