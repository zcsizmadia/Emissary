# Emissary Samples

Numbered in learning order — walk `01` → `09` to go from first tool to production deployment.
The full plan and the phase each sample lands in: see [ROADMAP.md](../ROADMAP.md#samples-plan-samples).

## Rules

- Every sample **builds in CI on every commit** — a breaking API change that breaks a sample fails
  the build. Samples are documentation that cannot rot.
- Samples are **excluded from coverage and mutation gates** (they demonstrate, they don't verify).
- Each sample has its own `README.md`: what it shows, how to run it, expected output.
- API keys come from the `ANTHROPIC_API_KEY` environment variable — never hardcoded.
- Samples that support it (04–06) run **fully offline via trajectory replay**, so readers without an
  API key still get a working experience.
- One shared constant defines the recommended Claude model; samples never scatter model ids.
