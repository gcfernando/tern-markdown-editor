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
