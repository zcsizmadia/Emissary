# ADR 0005 — Versioning and releases: tag-driven, lockstep

**Status:** accepted (2026-08)

## Decision

- **SemVer 2.0, all packages in lockstep.** Every shipped package carries the same version,
  always. Pre-1.0, minor bumps may break; from 1.0.0, strict semver.
- **Git tags are the single source of truth** (MinVer, `MinVerTagPrefix=v`). Tag `v1.2.3` →
  packages build as `1.2.3`. Untagged commits build as the next patch with an
  `-alpha.0.<height>` suffix — unique, traceable, never impersonating a release. No version
  is ever written into a file.
- **A release is pushing a tag.** `release.yml` triggers on `v*`, re-runs the full quality
  gauntlet (tests, coverage gate, AOT proof), packs, pushes to NuGet.org, and creates a GitHub
  release with generated notes and the packages attached.
- **One package carries the generator.** `Emissary` embeds `Emissary.SourceGenerators` under
  `analyzers/dotnet/cs`; the generator does not ship standalone. Shipped packages: `Emissary`,
  `Emissary.Testing`, `Emissary.AspNetCore`, `Emissary.Mcp`, `Emissary.Sqlite`.
- **Release hygiene:** SourceLink (SDK built-in), `.snupkg` symbol packages,
  `EnablePackageValidation` (set `PackageValidationBaselineVersion` after the first stable
  release so pack fails on unannounced API breaks), and a pack dry-run on every CI run.

The concrete steps and gates for the first non-preview release are kept in
[the release checklist](../release-checklist.md), including what must change in the docs at the tag
and which API shapes to settle before semver freezes them.

## Operational notes

- One-time setup: a NuGet.org API key scoped to `Emissary.*` stored as the `NUGET_API_KEY`
  repository secret; apply for `Emissary.` package-ID prefix reservation after first publish.
- The `[GeneratedCode]` version stamped into generated sources tracks the generator assembly
  version automatically.
- CI checkouts use `fetch-depth: 0` — MinVer needs tag history.
