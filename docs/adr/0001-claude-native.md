# ADR 0001 — Claude-native, not provider-agnostic

**Status:** accepted (2026-08)

## Decision

Emissary consumes the Claude API exclusively (via the official `Anthropic` SDK behind an internal
transport seam). The public API exposes Claude concepts (thinking blocks, cache control, compaction)
directly. We do not abstract over model providers.

Outward interop is first-class: Emissary agents are consumable via `IChatClient` and MCP, so they
compose into multi-model systems (e.g. Microsoft Agent Framework orchestrations) without Emissary
itself driving other models.

## Context

As of 2026: Microsoft Agent Framework 1.0 (GA April 2026) owns the provider-neutral .NET agent
space. The differentiated features Emissary is built on — cache-aware prompt assembly, compaction
lifecycle, block-level trajectory replay — live precisely in provider-specific details that a
neutral abstraction erases.

## Consequences

- Depth over breadth: one wire format, tested exhaustively.
- The provable-agents layer (contracts, taint, replay assertions) stays internally decoupled from
  Anthropic types so it *could* be extracted later — but no public multi-provider promise exists.
- The Anthropic SDK is beta; its types never appear in Emissary's public API surface.
