# Production

## Hosting over HTTP

```csharp
builder.Services.AddEmissary(SupportAgent.Configure);
builder.Services.AddSingleton<IAgentStateStore>(new SqliteAgentStateStore("Data Source=suspensions.db"));

var app = builder.Build();
app.MapEmissaryAgent("/agent");              // POST a message, stream the run as SSE
app.MapEmissaryApprovals("/agent/approvals"); // resume a suspended run
```

The SSE stream emits `text`, `thinking`, `tool_call`, `tool_result`, `tool_failed`, `handoff`,
`suspended`, `completed`, and — if the run faults after the response has been committed — `error`.
Suspensions are persisted automatically when an `IAgentStateStore` is registered, using a token the
client cannot cancel: a browser that disconnects at the moment of suspension must not lose a run
that is waiting for a human.

Responses set `X-Accel-Buffering: no` and disable response buffering, so a proxy in front of the app
does not hold the events and deliver the whole run in one burst.

The approval webhook **claims** a run before resuming it — resuming executes the privileged call a
human approved, so a retried or double-clicked webhook must not run it twice. The winner streams the
run; a later request gets `404`, and one that loses the race gets `409`. That makes approval
at-most-once by design: if a resume fails, the run is already consumed rather than replayable.
Sample [`07-WebApi`](https://github.com/zcsizmadia/Emissary/tree/main/samples/07-WebApi) ships a
Native AOT `Dockerfile`; [`05-SupportDesk`](https://github.com/zcsizmadia/Emissary/tree/main/samples/05-SupportDesk)
adds Postgres and an Aspire dashboard via `docker compose`.

## Durable chat sessions

```csharp
var session = new ConversationSession(agent, store, conversationId);
var reply = await session.SendAsync("hello");   // loads history, runs a turn, persists
```

`IConversationStore` has an in-memory implementation for tests and single-node apps, and
`SqliteConversationStore` for chat that must survive restarts.

## Resilience

```csharp
options.Resilience.MaxRetries = 3;
options.Resilience.BaseDelay = TimeSpan.FromMilliseconds(500);
options.Resilience.RequestTimeout = TimeSpan.FromSeconds(60);
```

Transient failures (HTTP errors, timeouts, rate limits, overloaded/5xx) are retried with capped
exponential backoff. Retries only happen while establishing the stream — once output has started
it is never re-issued — and genuine caller cancellation is never retried.

## Bounding tool concurrency

A single turn can ask for many tool calls, and by default they all execute at once — fast, but one
turn can open as many connections as the model asked for calls, which is how an agent drains a
database pool:

```csharp
options.MaxParallelTools = 4;
```

Calls beyond the cap queue and start as slots free. Results are still fed back in `tool_use` order,
so contracts, trajectories, and replay are unaffected — only the overlap changes. Leave it unset
when your tools are cheap and in-process.

## When a tool fails

`options.Resilience` covers calls to the API. `options.ToolFailures` covers *your* tools, which
also fail: a locked row, a 503 from a payment gateway, a query that never returns.

```csharp
options.ToolFailures.Timeout = TimeSpan.FromSeconds(30);
```

By default a handler that throws does not end the run. The model is told the tool failed and can
retry, try something else, or explain the problem to the user — the conversation, the token
accounting, and the contract state all survive. A tool that exceeds `Timeout` is cancelled (its
`CancellationToken` is signalled) and reported the same way. Cancelling the run yourself is *not* a
tool failure: that still surfaces as `OperationCanceledException`.

What the model is told is deliberately thinner than what you get:

```text
Tool 'charge_card' failed with HttpRequestException.
```

The exception's **message is withheld by default**, because messages carry connection strings, file
paths, SQL, and record data — and everything the model sees is sent to the API and may be repeated
in its reply to your user ([ADR 0007](../adr/0007-tool-failure-disclosure.md)). You still get the
exception in full:

```csharp
foreach (var failure in result.ToolFailures)
{
    logger.LogError(failure.Exception, "{Tool} failed (timeout: {TimedOut})",
        failure.ToolName, failure.TimedOut);
}
```

Streaming runs get an `AgentToolFailedEvent` as it happens, and the `execute_tool` activity records
`error.type` plus the message. Set `IncludeExceptionMessage = true` when the messages are safe and
the extra context helps the model recover.

A failed call never counts as a success for contracts: `Rules.Require("ship", "charge")` still
blocks shipping if the charge threw. When a failing tool means the whole run is untrustworthy,
switch to `ToolFailureMode.Propagate` and handle the exception yourself.

## Reading the stop reason

`AgentResult.StopReason` is the API's verdict, not a summary — check it before trusting
`FinalText`, because three of its values mean the answer is **incomplete**:

| Stop reason | What it means |
|---|---|
| `Completed` | The model finished. |
| `MaxTokens` | Cut off by `options.MaxTokens`, or by the context window. |
| `Refusal` | The model declined. |
| `Paused` | A server-side tool (web search) paused the turn mid-answer. |
| `TurnLimit` / `BudgetExceeded` | Emissary stopped the run at `MaxTurns` / `TokenBudget`. |
| `AwaitingApproval` | A gated call suspended the run; see `Suspension`. |

`Paused` is continuable — run the agent again on the conversation it returns:

```csharp
while (result.StopReason == AgentStopReason.Paused)
{
    result = await agent.RunAsync(result.Conversation);
}
```

## Prompt caching and cost

Caching is **on by default** (`PromptCacheMode.Automatic`): breakpoints go after the tool
definitions, on the system prompt, and on the latest message, so follow-up turns read the stable
prefix from cache. The savings are visible per run:

```csharp
result.Usage.CacheReadInputTokens      // served from cache
result.Usage.CacheCreationInputTokens  // written to cache
```

Turn tokens into money with your own contract rates:

```csharp
var estimator = new CostEstimator().Register("claude-opus-5", new ModelPricing(
    InputPerMillion: 15m, OutputPerMillion: 75m,
    CacheWritePerMillion: 18.75m, CacheReadPerMillion: 1.5m));

decimal cost = estimator.Estimate(options.Model, result.Usage);
```

Cap spend per run with `options.TokenBudget`; the run stops with
`AgentStopReason.BudgetExceeded` before the next model call.

## Long conversations

```csharp
options.Compaction.TriggerInputTokens = 120_000;
options.Compaction.KeepRecentMessages = 8;
```

When a turn's input passes the trigger, Emissary summarizes the older messages and continues with
the summary plus recent history, emitting `AgentCompactedEvent`. Compaction runs client-side on
purpose, so compacted runs still record and replay
([ADR 0006](../adr/0006-client-side-compaction.md)).

## Telemetry

Emissary emits OpenTelemetry GenAI spans (`invoke_agent`, `chat`, `execute_tool`,
`compact_context`) and metrics (token counters per tier, tool calls, run duration):

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Emissary").AddOtlpExporter())
    .WithMetrics(m => m.AddMeter("Emissary").AddOtlpExporter());
```

## Interop

- **MCP** — expose tools or a whole agent over the Model Context Protocol so Claude Code and
  Claude Desktop can call them (`Emissary.Mcp`).
- **Microsoft.Extensions.AI** — wrap an agent as an `IChatClient` and drop it into any .NET AI
  pipeline, including Microsoft Agent Framework orchestrations (`Emissary.Extensions.AI`).

```csharp
IChatClient client = new EmissaryChatClient(agent);
```

## Native AOT

Everything above is AOT-compatible. Publishing an agent yields a self-contained native binary
(~1.5 MB for the CI proof app, ~44 ms process lifetime) — see
[benchmarks](../benchmarks.md).
