# 04 — RecordReplay

The provable-agents workflow:

1. **Record** a live run: `new ClaudeAgent(options, recorder)` captures every model exchange
   into a `Trajectory`, saved as a `.trajectory` JSON file.
2. **Replay** it deterministically: `new ClaudeAgent(options, trajectory)` serves the recorded
   turns instead of calling the API — real tools still execute, zero network, byte-identical
   behavior every run. If the agent's requests drift from the recording (different model, tools,
   or conversation shape), replay fails with `TrajectoryDivergenceException`.
3. **Assert** on behavior with `Emissary.Testing`:
   `EmissaryAssert.That(result).ToolNotCalledBefore("refund_payment", "verify_identity")`.

## Run (offline — no API key needed)

```bash
dotnet run --project samples/04-RecordReplay
```

Replays the bundled `demo.trajectory` and proves the refund never happened before the
identity check.

## Re-record live

```bash
export ANTHROPIC_API_KEY=...   # PowerShell: $env:ANTHROPIC_API_KEY = "..."
dotnet run --project samples/04-RecordReplay -- --record
```

Runs the same scenario against the real API and writes a fresh `demo.trajectory` next to the
binary.
