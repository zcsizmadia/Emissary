# ADR 0002 — TUnit on Microsoft.Testing.Platform

**Status:** accepted (2026-08)

## Decision

All tests use TUnit running in Microsoft.Testing.Platform (MTP) mode of `dotnet test`
(opted in via the `test.runner` setting in `global.json`; requires .NET 10 SDK).

## Context

TUnit is source-generator-based and Native AOT-compatible — the same ethos as Emissary itself
(no reflection, compile-time wiring). MTP is the modern test runner; VSTest mode is legacy for
MTP projects on the .NET 10 SDK.

## Consequences

- Test projects are executables (`OutputType=Exe`).
- Coverage is collected by `Microsoft.Testing.Extensions.CodeCoverage`
  (`--coverage` platform option), not coverlet.
- Stryker.NET cannot run MTP test projects yet
  (stryker-mutator/stryker-net#3094) — see ADR 0003.
