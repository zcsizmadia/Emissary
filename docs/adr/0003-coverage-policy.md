# ADR 0003 — Coverage policy: 100%, honestly

**Status:** accepted (2026-08)

## Decision

CI enforces **100% line and 100% branch coverage** on shipped libraries
(`build/check-coverage.ps1` over ReportGenerator's JsonSummary, filtered to `+Emissary*;-*.Tests`).

Permitted exclusions, and nothing else:

1. Compiler-generated code without sequence points.
2. `[ExcludeFromCodeCoverage]` **with a justification comment on the attribute** — reviewed like
   code. Legitimate uses: defensive throws that are unreachable by construction, platform-specific
   branches CI cannot execute.

Coverage proves code ran; it does not prove tests assert anything. Mutation testing is the intended
quality guard, but Stryker.NET cannot run Microsoft.Testing.Platform (TUnit) tests
(stryker-mutator/stryker-net#3094), so no Stryker tooling is kept in the repo — dead config rots.
**Re-adopt when that issue closes** (tool install + config + a PR-only CI job with
`thresholds.break = 100` is ~20 minutes of work). Until then, reviews watch for assertion-free
tests manually.

## Consequences

- A single uncovered line fails the build — by design, from the first commit.
- Samples and test projects are outside the gates.
- New code ships with its tests in the same PR; there is no "add tests later" state.
