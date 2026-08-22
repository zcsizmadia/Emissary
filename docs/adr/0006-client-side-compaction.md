# ADR 0006 — Client-side context compaction, not the server-side beta

**Status:** accepted (2026-08)

## Decision

When a conversation outgrows the context window, Emissary compacts it **itself**: it summarizes
the older messages with one extra, ordinary model call and replaces them with that summary
(`CompactionOptions`, `AgentCompactedEvent`). We do **not** use the Anthropic beta context-
management/compaction edits (`BetaCompact20260112Edit`, `BetaClearToolUses20250919Edit`).

## Context

The SDK exposes server-side compaction, but only on the beta endpoint, which brings two costs:

1. **Opacity breaks the thesis.** Server-side compaction rewrites history inside the API. A
   recorded trajectory would no longer describe what the model actually saw, so replay could not
   reproduce a compacted run and divergence detection would produce false results — the exact
   guarantees Emissary exists to provide ([ADR 0001](0001-claude-native.md)).
2. **A parallel mapper.** The beta endpoint uses a separate type namespace (`BetaMessageParam`,
   `BetaContentBlockParam`, …), so supporting it means a second full request/response mapper and
   a second streaming shell — doubling the surface that the coverage policy cannot reach
   ([ADR 0003](0003-coverage-policy.md)).

Client-side compaction, by contrast: appears in the trajectory as a normal recorded turn, replays
deterministically, is observable (`AgentCompactedEvent`, `compact_context` span), and is fully
unit-testable offline.

## Consequences

- Compaction costs one extra model call, and the summary quality is the caller's to tune via
  `CompactionOptions.SummaryInstruction`.
- The cut point is always an **assistant** message, so a tool-use turn is never separated from the
  turn carrying its tool results, and roles still alternate after the summary is inserted.
- Compaction is opt-in (`TriggerInputTokens`), off by default.
- If the beta compaction becomes non-beta *and* the API reports what it removed in a way that can
  be recorded, revisit: server-side would then be cheaper without losing auditability.
