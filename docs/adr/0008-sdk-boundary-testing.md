# ADR 0008 — Tests at the SDK boundary must use SDK-produced values

**Status:** accepted (2026-08)

## Decision

Any code that interprets a value coming out of the Anthropic SDK must have a test whose input is
produced **by the SDK itself** — deserialized from a real wire frame, or constructed through the
SDK's own types — never a hand-written string that we believe resembles what the SDK produces.

## Context

Emissary's whole offline story rests on `IModelTransport`: fake it, and 400+ tests run
deterministically with no network ([ADR 0002](0002-tunit-mtp.md)). That seam is also a blind spot.
Everything above it is tested against values *we* invent, so any mistaken belief about what the SDK
actually emits is invisible to the entire suite.

Twice now the same bug has shipped through that blind spot:

1. The agent loop compared the stop reason to `"tool_use"`, while `ToString()` on the SDK enum was
   believed to yield `"ToolUse"`. Tools were never executed against the live API; every offline test
   passed. The fix normalized `ToString()`, and a test asserted `"ToolUse" → "tool_use"`.
2. That fix was also wrong. `ApiEnum<string, StopReason>.ToString()` renders the **JSON** form —
   `"tool_use"`, quote characters included — so normalization matched nothing and every stop reason
   became `end_turn`. `AgentStopReason.MaxTokens` and `Refusal` were unreachable in production for
   as long as that code existed, and a truncated answer or a refusal reported as `Completed`. Tool
   calling survived only because a later heuristic infers `tool_use` from the assembled content —
   the safety net masked the failure it was meant to catch.

The second bug is the instructive one: a test *existed* for exactly this function, and it passed,
because it asserted against a guess. Reviewing the assertion could never have found the defect; only
comparing it with the SDK could.

`ResilienceTests` shows the same shape in another place: it asserts transient-error classification
against `FakeRateLimitException` and `ServiceUnavailableException`, classes the SDK never throws.

## Consequences

- Tests for boundary code deserialize a realistic frame and assert on what the SDK yields.
  `AnthropicMapperTests.The_stop_reason_the_sdk_deserializes_normalizes_correctly` is the pattern:
  it feeds a `message_delta` through the SDK's deserializer and checks both `Raw()` and `ToString()`,
  so a refactor that reaches for the wrong one cannot pass.
- Normalization at the boundary stays lenient — wire form, JSON form, and PascalCase all map
  correctly — because the SDK is beta and its rendering has already changed once. Leniency is the
  belt; the SDK-derived test is the braces.
- A heuristic that repairs a boundary mistake (inferring `tool_use` from content blocks) must not be
  the only thing standing between a bug and production. Keep it, and test the thing it protects.
- Live smoke runs (`.github/workflows/live-smoke.yml`) remain the last line of defence, not the
  first: they need credentials and cost money, so they cannot be the PR gate.
