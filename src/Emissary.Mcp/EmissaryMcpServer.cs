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
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error.");
        }

        // JsonNode's indexer throws for arrays and scalars, so anything that is not an object has
        // to be rejected before a member is read. A batch (a JSON array) is legal JSON-RPC that this
        // server does not implement; previously it — and any bare scalar — threw out of the read
        // loop and killed the process, taking the whole session with it.
        if (parsed is not JsonObject request)
        {
            return Error(null, -32600, "Invalid request: expected a single JSON-RPC request object.");
        }

        JsonNode? id = request["id"];
        string method = AsString(request["method"]) ?? "";

        if (method.StartsWith("notifications/", StringComparison.Ordinal) || id is null)
        {
            return null;
        }

        try
        {
            return method switch
            {
                "initialize" => Result(id, new JsonObject
                {
                    ["protocolVersion"] = AsString((request["params"] as JsonObject)?["protocolVersion"])
                        ?? ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = _options.Name, ["version"] = _options.Version },
                }),

                // The spec requires a response; an empty result is the whole of it.
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = DescribeTools() }),
                "tools/call" => await CallToolAsync(id, request["params"] as JsonObject, cancellationToken)
                    .ConfigureAwait(false),
                _ => Error(id, -32601, $"Unknown method '{method}'."),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One malformed request must cost one response, never the session.
            return Error(id, -32603, $"Internal error handling '{method}': {exception.GetType().Name}.");
        }
    }

    /// <summary>Reads a node as a string, or <see langword="null"/> if it is any other JSON kind.</summary>
    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

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

    private async Task<JsonNode> CallToolAsync(JsonNode id, JsonObject? parameters, CancellationToken cancellationToken)
    {
        string name = AsString(parameters?["name"]) ?? "";
        JsonObject arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();

        try
        {
            if (_options.Agent is not null && name == _options.AgentToolName)
            {
                string? message = AsString(arguments["message"]);
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
            // A tool that throws is a failed tool call, not a broken protocol: the spec wants an
            // isError result so the calling model can react, and -32603 gave it none. The type
            // rather than the message, for the reason in ADR 0007 — this text lands in a model's
            // context, and exception messages carry connection strings and record data.
            return ToolResult(id, $"Tool '{name}' failed with {exception.GetType().Name}.", isError: true);
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
