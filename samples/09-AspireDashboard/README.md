# 09 — Emissary in the Aspire dashboard

Emissary emits OpenTelemetry GenAI traces and metrics whether or not anything is listening. This
sample makes something listen, and the result is that every model call, tool call, token and
millisecond shows up in a dashboard you did not build.

```bash
dotnet tool install -g aspire.cli          # once
dotnet user-secrets --project samples/09-AspireDashboard/AppHost \
  set Parameters:anthropic-api-key sk-ant-...
aspire run --project samples/09-AspireDashboard/AppHost
```

Then open the dashboard it prints, and:

```bash
curl -N localhost:<port>/agent -H 'content-type: application/json' \
  -d '{"message":"What is the weather in Oslo and in Bergen?"}'
```

## What to look at

**Traces.** One `invoke_agent` span per run, with a `chat` span for every model call and an
`execute_tool` span for every tool call nested inside it. Ask about two cities and the parallel tool
calls appear side by side — which is how you find out whether an agent is actually working in
parallel or only claiming to.

**Metrics.** `emissary.usage.input_tokens`, `output_tokens`, and both cache counters, so a cache hit
rate is a division rather than a guess. Plus `emissary.run.duration` and — new — 
`emissary.tool.duration`, tagged by tool name and outcome. Tool latency is where an agent's
wall-clock time actually goes, and a tool that has become slow is invisible in run duration alone:
the model narrates around it and the answer still arrives.

**Health.** `/health` reports whether this process is *configured* to run an agent — a missing key or
an empty model — and deliberately makes no API call. A health check that talked to the model would
bill you for being alive, and would report Anthropic's health rather than your own.

## The three lines that matter

```csharp
builder.AddEmissaryAgent(options => options.Tools.Add(WeatherTools.GetForecastTool));
builder.Services.AddOpenTelemetry().UseOtlpExporter();
builder.Services.AddHealthChecks();
```

`AddEmissaryAgent` binds the agent's settings from configuration, subscribes Emissary's
`ActivitySource` and `Meter` to OpenTelemetry, and registers the health check. Forgetting that
subscription is the single most common reason an agent looks untraced — which is indistinguishable
from an agent that is not running.

The OTLP endpoint is injected by the app host, so the service contains no endpoint configuration and
no API key.

## Notes

- `Emissary.Aspire` takes **no dependency on Aspire**. It follows the client-integration
  conventions, so it works in any .NET host — plain `dotnet run`, a container, Kubernetes — and
  lights the dashboard up when there is one.
- The service half runs standalone: `dotnet run --project samples/09-AspireDashboard/Service`, then
  `GET /` and `GET /health`. Only the dashboard needs the Aspire CLI, which brings the orchestrator
  the app host cannot supply by itself (the project deliberately does not pull that bundle in, since
  CI builds this sample on every commit).
- Settings can come from configuration instead of code:
  `Emissary:Model`, `Emissary:MaxTurns`, `Emissary:TokenBudget`, `Emissary:Thinking`, … A misspelled
  value fails at startup rather than falling back to a default, because a token budget that silently
  becomes "unlimited" is discovered on an invoice.
