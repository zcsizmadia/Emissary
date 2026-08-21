namespace Emissary.Mcp;

/// <summary>Configuration for an <see cref="EmissaryMcpServer"/>.</summary>
public sealed class EmissaryMcpServerOptions
{
    /// <summary>The server name reported to MCP hosts.</summary>
    public string Name { get; set; } = "emissary";

    /// <summary>The server version reported to MCP hosts.</summary>
    public string Version { get; set; } = "0.1.0";

    /// <summary>
    /// Tools exposed directly over MCP — the same <see cref="ToolDefinition"/>s the source
    /// generator produces. Each runs locally in this process; no Claude API key is needed.
    /// </summary>
    public IList<ToolDefinition> Tools { get; } = [];

    /// <summary>
    /// Optional agent exposed as a single MCP tool (<see cref="AgentToolName"/>): the caller sends
    /// a message, the whole agent loop runs, and the final text comes back.
    /// </summary>
    public ClaudeAgent? Agent { get; set; }

    /// <summary>The wire name of the agent tool.</summary>
    public string AgentToolName { get; set; } = "ask_agent";

    /// <summary>The description of the agent tool shown to MCP hosts.</summary>
    public string AgentToolDescription { get; set; } =
        "Send a message to the agent and get its final answer.";
}
