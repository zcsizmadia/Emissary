# Production

## Hosting over HTTP

```csharp
builder.Services.AddEmissary(SupportAgent.Configure);
builder.Services.AddSingleton<IAgentStateStore>(new SqliteAgentStateStore("Data Source=suspensions.db"));

var app = builder.Build();
app.MapEmissaryAgent("/agent");              // POST a message, stream the run as SSE
app.MapEmissaryApprovals("/agent/approvals"); // resume a suspended run
```

The SSE stream emits `text`, `thinking`, `tool_call`, `tool_result`, `suspended`, and `completed`
events. Suspensions are persisted automatically when an `IAgentStateStore` is registered.
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
