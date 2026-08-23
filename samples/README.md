# Emissary Samples

Numbered in learning order — walk `01` → `09` to go from first tool to production deployment.
The full plan and the phase each sample lands in: see [ROADMAP.md](../ROADMAP.md#samples-plan-samples).

## Rules

- Every sample **builds in CI on every commit** — a breaking API change that breaks a sample fails
  the build. Samples are documentation that cannot rot.
- Samples are **excluded from coverage and mutation gates** (they demonstrate, they don't verify).
- Each sample has its own `README.md`: what it shows, how to run it, expected output.
- API keys come from the `ANTHROPIC_API_KEY` environment variable — never hardcoded.
- **Samples are cost-capped.** Every sample applies `SampleBudget`: a small model, a hard
  `TokenBudget`, and a low `MaxTurns`, so one `dotnet run` costs a fraction of a cent and a sample
  that gets stuck stops instead of spending. See [Cost](#cost) below — this is not Emissary's
  default, it is the samples' choice.
- Samples that support it (04–06) run **fully offline via trajectory replay**, so readers without an
  API key still get a working experience.
- One shared constant defines the recommended Claude model; samples never scatter model ids.

## Cost

Running a sample calls the real API and spends real money. Emissary's own defaults are tuned for an
agent doing real work, and they are **not** cheap:

| Default | Value | Why it matters for a demo |
|---|---|---|
| `Model` | `claude-opus-5` | the most expensive tier |
| `MaxTurns` | 16 | up to 16 model calls per run, each resending a growing conversation |
| `TokenBudget` | *unset* | **no ceiling on spend** |

One run that loops near the turn limit on Opus can cost on the order of a dollar. So every sample
overrides all three through `SampleBudget` (`samples/SampleBudget.cs`, linked into each project):

```csharp
Model = SampleBudget.Model,             // claude-haiku-4-5
MaxTurns = SampleBudget.MaxTurns,       // 6
TokenBudget = SampleBudget.TokenBudget, // 50k tokens, then the run stops
```

Two exceptions: `04-RecordReplay` and `06-ZeroTrustAgent` keep the default model, because their
committed `.trajectory` files were recorded with it and replay verification rejects a mismatch —
proof that the guard works, and a reason to re-record them cheaply later. They still take the budget
cap and the turn limit.

**For your own agents**, set `options.TokenBudget` before running anything unattended, and price runs
locally with `CostEstimator` (see the
[production guide](../docs/guides/production.md#prompt-caching-and-cost)). The authoritative figure
is always the usage page in the Anthropic Console, per day and per model.
