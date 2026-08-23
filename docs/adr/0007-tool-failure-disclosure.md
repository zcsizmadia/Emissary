# ADR 0007 — A failed tool tells the model less than it tells the caller

**Status:** accepted (2026-08)

## Decision

When a tool handler throws, the run continues by default: the failure becomes an error tool result
the model can act on (`ToolFailureMode.ReportToModel`). What the model is told is **the exception
type only** — `Tool 'charge_card' failed with HttpRequestException.` The exception's message is
withheld unless the caller sets `ToolFailureOptions.IncludeExceptionMessage`.

The exception itself is never discarded. It reaches the caller in full through
`AgentResult.ToolFailures`, an `AgentToolFailedEvent` on the stream, and the `execute_tool`
activity (`error.type` plus the message).

## Context

Two questions had to be answered together, and the answers pull against each other.

**Should a throwing tool end the run?** Before this, any exception other than
`ToolArgumentException` escaped the agent loop. A 503 from a payment gateway therefore destroyed the
conversation, the token accounting, the taint state, and the plan of effects — everything the
framework exists to make auditable. A tool failing is an operational event, and a model told about
it can retry, choose another tool, or explain the problem. Continuing is the better default;
`Propagate` remains for runs where a failed tool means the result cannot be trusted.

**How much of the failure may the model see?** Continuing means putting failure text into the
prompt, and everything in the prompt is sent to the API and may be repeated verbatim in the model's
reply to an end user. Exception messages are written for developers reading logs: they routinely
carry connection strings, credentials embedded in URIs, absolute paths, SQL fragments, and the
contents of the row being processed. Forwarding them by default would make the most convenient
setting the one that leaks — and it would leak into the least controllable place, a model's free
text.

The type name is a deliberate middle: enough for the model to reason about whether retrying is
sensible (`TimeoutException` versus `UnauthorizedAccessException`), and drawn from a vocabulary
chosen by the developer, not from runtime data.

## Consequences

- The safe configuration is the default one. Sharing more is a visible opt-in per agent.
- Diagnosing a tool failure means looking at the caller's side — logs, the event, or the span — not
  at the transcript. `ToolFailure.TimedOut` distinguishes a cancelled slow tool from a throw.
- A timeout (`ToolFailureOptions.Timeout`) is reported through the same path, and says how long the
  tool was given, which is caller-supplied configuration rather than runtime data.
- Caller cancellation stays cancellation: `OperationCanceledException` propagates and is not
  reported as a tool failure, so a cancelled run is never mistaken for a broken tool.
- A failed call still never satisfies a contract prerequisite ([ADR 0001](0001-claude-native.md)'s
  provability thesis): `Rules.Require("ship", "charge")` blocks shipping if the charge threw.
- Trajectories record what the model saw, so a recorded run stays replayable regardless of which
  disclosure setting produced it.
