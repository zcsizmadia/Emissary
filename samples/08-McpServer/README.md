# 08 — McpServer

Emissary as a Model Context Protocol server: the source-generated C# tools (and optionally a
whole agent) become MCP tools callable from Claude Code, Claude Desktop, or any MCP host.

- **Tools mode (no API key)**: `roll_dice` and `convert_temperature` run locally in-process.
- **Agent mode (with `ANTHROPIC_API_KEY`)**: an extra `ask_emissary` tool runs the full agent
  loop and returns the final answer.

## Register with Claude Code

```bash
claude mcp add emissary-demo -- dotnet run --project samples/08-McpServer
```

Then in a Claude Code session: *"use the emissary-demo tools to roll 3 dice"*.

## Try it by hand

The MCP stdio transport is newline-delimited JSON-RPC:

```bash
dotnet run --project samples/08-McpServer <<'EOF'
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"convert_temperature","arguments":{"value":21.5}}}
EOF
```
