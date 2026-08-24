# 11 — Tools from an OpenAPI specification

An agent driving a real public API with no hand-written tool code.

```bash
dotnet run --project samples/11-OpenApiTools
```

The tool half needs no credentials: [Open-Meteo](https://open-meteo.com) requires no API key, so the
sample generates the tool, prints its schema, and calls it for real. Set `ANTHROPIC_API_KEY` to also
let the model choose the arguments itself.

## What to look at

**The specification is the only input.** The tool's name, description, input schema and parameter
documentation are all read out of the document. Nothing in `Program.cs` describes the API.

**The safety posture is derived, not configured.** A specification already says which operations read
and which ones write. `Emissary.OpenApi` turns that into `Untrusted` on reads and `Privileged` on
writes, and Emissary's existing taint tracking then enforces a rule nobody wrote down: having read a
response body — content someone else authored — the agent can no longer write back through the same
API. The run prints `tainted: true` after the forecast call for exactly that reason.

This one specification is read-only, so nothing gets blocked. Point the same code at a specification
with a `POST` and the interlock is what stops a page of attacker-controlled JSON from talking the
agent into a write.

**Selection matters.** A large public specification will generate hundreds of tools, and a prompt
carrying hundreds of tool schemas is expensive and worse at choosing. `MaxTools` defaults to 64 and
throws when exceeded; filter with `Tags` or `OperationIds` instead of raising it.

See the [tools guide](../../docs/guides/tools.md#tools-from-an-openapi-specification) for the rest,
including what the reader deliberately will not do — headers, YAML, remote `$ref`, non-JSON bodies.
