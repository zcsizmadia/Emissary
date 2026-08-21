# ADR 0004 — Single target: net10.0, modern C# first

**Status:** accepted (2026-08)

## Decision

All projects target `net10.0` only, `LangVersion` latest (C# 14). New TFMs (net11.0+) are added
only when a concrete feature earns it; old TFMs are never added.

## Context

At decision time (Aug 2026): .NET 9 is already end-of-life (STS, May 2026); .NET 8 leaves support
Nov 2026 — before v1 ships; .NET 10 is the current LTS (Nov 2028). Several capabilities the design
leans on are runtime-gated, not just compiler-gated: `allows ref struct` anti-constraints,
`params ReadOnlySpan<T>`, `System.Threading.Lock`, C# 14 extension members and `field` properties.

## Consequences

- No `#if` forests, no lowest-common-denominator API shapes.
- One TFM halves the CI/coverage matrix (which enforces 100% — see ADR 0003).
- Native AOT compatibility (`IsAotCompatible`) is asserted on every build of shipped libraries.
