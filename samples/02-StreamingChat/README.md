# 02 — StreamingChat

A multi-turn console chat: `StreamAsync` yields thinking deltas (rendered dim), text deltas,
and a final `AgentCompletedEvent` whose result carries the updated immutable `Conversation` —
carrying that forward is the entire multi-turn state story.

## Run

```bash
export ANTHROPIC_API_KEY=...   # PowerShell: $env:ANTHROPIC_API_KEY = "..."
dotnet run --project samples/02-StreamingChat
```

Type messages; an empty line exits.
