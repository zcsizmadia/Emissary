# 01 — HelloTool

The smallest possible Emissary agent: two `[ClaudeTool]` static methods, one `RunAsync` call.
The source generator turns the attributed methods into `RollDiceTool` / `GetTimeTool` properties
with compile-time JSON Schemas and reflection-free dispatchers — descriptions come from the
XML doc comments.

## Run

```bash
export ANTHROPIC_API_KEY=...   # PowerShell: $env:ANTHROPIC_API_KEY = "..."
dotnet run --project samples/01-HelloTool
dotnet run --project samples/01-HelloTool -- what time is it?
```

## Native AOT

```bash
dotnet publish samples/01-HelloTool -c Release
```

Produces a small self-contained native executable — no runtime, no reflection.
