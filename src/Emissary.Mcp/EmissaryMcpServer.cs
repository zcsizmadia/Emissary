using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Emissary.Mcp;

/// <summary>
/// A minimal Model Context Protocol server over newline-delimited JSON-RPC (the MCP stdio
/// transport). Exposes the configured <see cref="ToolDefinition"/>s — and optionally a whole
/// agent — as MCP tools. Hand-rolled rather than SDK-based to stay dependency-free and
/// Native AOT-safe (JsonNode only, no reflection serialization).
/// </summary>
public sealed class EmissaryMcpServer
{
    private const string ProtocolVersion = "2025-03-26";

    private readonly EmissaryMcpServerOptions _options;

    /// <summary>Creates the server.</summary>
    /// <param name="options">What to expose; must contain at least one tool or an agent.</param>
    public EmissaryMcpServer(EmissaryMcpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Tools.Count == 0 && options.Agent is null)
        {
            throw new ArgumentException("Expose at least one tool or an agent.", nameof(options));
        }

        _options = options;
    }

    /// <summary>
    /// Serves MCP over the given streams (typically <c>Console.OpenStandardInput()</c> /
    /// <c>Console.OpenStandardOutput()</c>) until the input ends.
    /// </summary>
    /// <param name="input">The request stream.</param>
    /// <param name="output">The response stream.</param>
    /// <param name="cancellationToken">Stops the server.</param>
    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        using var reader = new StreamReader(input, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonNode? response = await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                await writer.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);
            }
        }
    }

    internal async Task<JsonNode?> HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        JsonNode? request;
        try
        {
            request = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error.");
        }

        JsonNode? id = request?["id"];
        string method = request?["method"]?.GetValue<string>() ?? "";

        if (method.StartsWith("notifications/", StringComparison.Ordinal) || id is null)
        {
            return null;
        }

        // A non-null id implies a non-null request (the id was read from it).
        return method switch
        {
            "initialize" => Result(id, new JsonObject
            {
                ["protocolVersion"] = request!["params"]?["protocolVersion"]?.GetValue<string>() ?? ProtocolVersion,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = _options.Name, ["version"] = _options.Version },
            }),
            "tools/list" => Result(id, new JsonObject { ["tools"] = DescribeTools() }),
            "tools/call" => await CallToolAsync(id, request?["params"], cancellationToken).ConfigureAwait(false),
            _ => Error(id, -32601, $"Unknown method '{method}'."),
        };
    }

    private JsonArray DescribeTools()
    {
        var tools = new JsonArray();
        if (_options.Agent is not null)
        {
            tools.Add((JsonNode)new JsonObject
            {
                ["name"] = _options.AgentToolName,
                ["description"] = _options.AgentToolDescription,
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["message"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("message"),
                },
            });
        }

        foreach (var tool in _options.Tools)
        {
            tools.Add((JsonNode)new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = JsonNode.Parse(tool.InputSchemaJson),
            });
        }

        return tools;
    }

    private async Task<JsonNode> CallToolAsync(JsonNode id, JsonNode? parameters, CancellationToken cancellationToken)
    {
        string name = parameters?["name"]?.GetValue<string>() ?? "";
        JsonNode arguments = parameters?["arguments"] ?? new JsonObject();

        try
        {
            if (_options.Agent is not null && name == _options.AgentToolName)
            {
                string? message = arguments["message"]?.GetValue<string>();
                if (string.IsNullOrEmpty(message))
                {
                    return ToolResult(id, "The 'message' argument is required.", isError: true);
                }

                var result = await _options.Agent.RunAsync(message, cancellationToken).ConfigureAwait(false);
                return result.StopReason == AgentStopReason.Completed
                    ? ToolResult(id, result.FinalText, isError: false)
                    : ToolResult(id, $"The agent stopped with {result.StopReason}: {result.FinalText}", isError: true);
            }

            var tool = _options.Tools.FirstOrDefault(t => t.Name == name);
            if (tool is null)
            {
                return Error(id, -32602, $"Unknown tool '{name}'.");
            }

            using var document = JsonDocument.Parse(arguments.ToJsonString());
            string content = await tool.Handler(document.RootElement, cancellationToken).ConfigureAwait(false);
            return ToolResult(id, content, isError: false);
        }
        catch (ToolArgumentException exception)
        {
            return ToolResult(id, exception.Message, isError: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Error(id, -32603, exception.Message);
        }
    }

    private static JsonObject ToolResult(JsonNode id, string text, bool isError) => Result(id, new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    });

    // Only reachable with a non-null id: id-less requests are notifications and get no response.
    private static JsonObject Result(JsonNode id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
