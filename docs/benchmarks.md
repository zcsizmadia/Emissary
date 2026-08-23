# Benchmarks

Measured with BenchmarkDotNet v0.15.8 (`--job short`) on Windows 11 x64, .NET 10, Release.
Reproduce with:

```bash
dotnet run --project benchmarks/Emissary.Benchmarks -c Release -- --filter '*'
```

## Tool machinery (the zero-reflection claim, measured)

| Benchmark | Mean | Allocated |
|---|---:|---:|
| `DispatchTool` — validate and bind JSON args, invoke, convert result (full generated dispatch path) | **~150 ns** | 112 B |
| `SchemaAccess` — read a tool's JSON Schema | **~0.4 ns** | 0 B |

The schema is a compile-time constant; dispatch is a handful of `JsonElement` reads and a
delegate call. There is no reflection to warm up and nothing to cache.

That figure now includes [argument validation](guides/tools.md#when-the-model-sends-the-wrong-type) —
every bound value is checked against its declared type before it is read. Re-measured after adding
it: ~150 ns against ~153 ns before, with allocation unchanged at 112 B. So the check that turns a
wrong-typed argument into a repairable error instead of a dead run is free, which is why it is not
optional.

## Record/replay machinery

| Benchmark | Mean |
|---|---:|
| `ReplayToolLoopRun` — a complete two-turn agent run (tool call + final answer), zero network | **~4.2 µs** |
| `Deserialize` — parse a two-turn `.trajectory` file | ~8.3 µs |
| `SerializeRoundTrip` — trajectory → JSON → trajectory | ~12.0 µs |

A full deterministic agent run costs about four microseconds — golden-trajectory regression
suites are effectively free at any scale (a thousand replayed scenarios ≈ 4 ms of agent-loop
time).

## Native AOT (from the CI-enforced proof binary)

| Metric | Value |
|---|---:|
| Self-contained binary size (agent runtime + 5 generated tools + schema) | **1.53 MB** |
| Full process lifetime: start → 5 tool executions (incl. async) → exit | **~44–56 ms** |

Measured on `tests/Emissary.AotProof` (win-x64, `InvariantGlobalization`), the binary CI
publishes and executes on every commit.

## Honest caveats

- `--job short` (3 iterations) — treat the numbers as orders of magnitude, not lab results.
- Everything above deliberately excludes the network: in a live agent, model latency
  (seconds) dominates by 5–6 orders of magnitude. The point of these numbers is that
  Emissary's own machinery is never your bottleneck — and that replay-based testing is
  fast enough to run everywhere, constantly.
