# 03 — TypedOutputs

Structured outputs, typed end to end at compile time:

1. `[ClaudeSchema]` on the `TicketTriage` record generates a strict JSON Schema
   (`additionalProperties: false`, doc-comment descriptions) as `TicketTriage.JsonSchema`.
2. `AgentOptions.OutputSchemaJson` sends it to the structured-outputs API, so the final answer
   is guaranteed to conform.
3. `result.FinalAs(SampleJsonContext.Default.TicketTriage)` deserializes with System.Text.Json's
   source generator — no reflection anywhere in the pipeline, fully Native AOT-safe.

## Run

```bash
export ANTHROPIC_API_KEY=...   # PowerShell: $env:ANTHROPIC_API_KEY = "..."
dotnet run --project samples/03-TypedOutputs
```
