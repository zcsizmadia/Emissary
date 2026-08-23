using System.Text;
using System.Text.Json.Nodes;
using Emissary.Mcp;
using Emissary.Tests.Agents;
using Emissary.Tests.Tools;
using Emissary.Transport;

namespace Emissary.Tests;

public sealed class McpServerTests
{
    private static EmissaryMcpServer CreateToolServer() =>
        new(new EmissaryMcpServerOptions { Tools = { SampleTools.EchoTool, SampleTools.AddTool } });

    private static async Task<string> RoundTripAsync(EmissaryMcpServer server, params string[] requestLines)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', requestLines) + "\n"));
        using var output = new MemoryStream();
        await server.RunAsync(input, output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static JsonNode Response(string body, int index = 0) =>
        JsonNode.Parse(body.Split('\n', StringSplitOptions.RemoveEmptyEntries)[index])!;

    [Test]
    public async Task Initialize_reports_server_info_and_tools_capability()
    {
        string body = await RoundTripAsync(CreateToolServer(),
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""");

        var response = Response(body);
        await Assert.That(response["result"]!["protocolVersion"]!.GetValue<string>()).IsEqualTo("2025-03-26");
        await Assert.That(response["result"]!["serverInfo"]!["name"]!.GetValue<string>()).IsEqualTo("emissary");
        await Assert.That((object?)response["result"]!["capabilities"]!["tools"]).IsNotNull();
    }

    [Test]
    public async Task Initialize_defaults_the_protocol_version()
    {
        string body = await RoundTripAsync(CreateToolServer(),
            """{"jsonrpc":"2.0","id":1,"method":"initialize"}""");

        await Assert.That(Response(body)["result"]!["protocolVersion"]!.GetValue<string>())
            .IsEqualTo("2025-03-26");
    }

    [Test]
    public async Task Tools_list_includes_generated_tools_with_their_schemas()
    {
        string body = await RoundTripAsync(CreateToolServer(),
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        var tools = (JsonArray)Response(body)["result"]!["tools"]!;
        await Assert.That(tools.Count).IsEqualTo(2);
        await Assert.That(tools[0]!["name"]!.GetValue<string>()).IsEqualTo("echo");
        await Assert.That(tools[0]!["inputSchema"]!["required"]![0]!.GetValue<string>()).IsEqualTo("text");
    }

    [Test]
    public async Task Tools_call_executes_a_generated_tool()
    {
        string body = await RoundTripAsync(CreateToolServer(),
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"add","arguments":{"left":2,"right":5}}}""");

        var result = Response(body)["result"]!;
        await Assert.That(result["isError"]!.GetValue<bool>()).IsFalse();
        await Assert.That(result["content"]![0]!["text"]!.GetValue<string>()).IsEqualTo("7");
    }

    [Test]
    public async Task Invalid_tool_arguments_return_an_error_result()
    {
        string body = await RoundTripAsync(CreateToolServer(),
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"echo","arguments":{}}}""");

        var result = Response(body)["result"]!;
        await Assert.That(result["isError"]!.GetValue<bool>()).IsTrue();
        await Assert.That(result["content"]![0]!["text"]!.GetValue<string>()).Contains("missing required argument");
    }

    [Test]
    public async Task Unknown_tool_and_method_return_jsonrpc_errors()
    {
        string body = await RoundTripAsync(CreateToolServer(),
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"nope"}}""",
            """{"jsonrpc":"2.0","id":6,"method":"prompts/list"}""");

        await Assert.That(Response(body, 0)["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32602);
        await Assert.That(Response(body, 1)["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32601);
    }

    [Test]
    public async Task Notifications_and_blank_lines_get_no_response()
    {
        string body = await RoundTripAsync(CreateToolServer(),
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            "",
            """{"jsonrpc":"2.0","method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":7,"method":"tools/list"}""");

        await Assert.That(body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Malformed_json_returns_a_parse_error()
    {
        string body = await RoundTripAsync(CreateToolServer(), "{nope");

        await Assert.That(Response(body)["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32700);
    }

    [Test]
    public async Task Degenerate_requests_are_handled()
    {
        string body = await RoundTripAsync(CreateToolServer(),
            "null",
            """{"jsonrpc":"2.0","id":20}""",
            """{"jsonrpc":"2.0","id":21,"method":"initialize","params":{}}""",
            """{"jsonrpc":"2.0","id":22,"method":"tools/call"}""",
            """{"jsonrpc":"2.0","id":23,"method":"tools/call","params":{}}""");

        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Length).IsEqualTo(5);

        // A bare "null" parses but is not a request object, which is Invalid Request rather than
        // silence — and, before this was guarded, an exception that killed the server.
        await Assert.That(Response(body, 0)["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32600);
        await Assert.That(Response(body, 1)["error"]!["message"]!.GetValue<string>()).Contains("Unknown method");
        await Assert.That(Response(body, 2)["result"]!["protocolVersion"]!.GetValue<string>()).IsEqualTo("2025-03-26");
        await Assert.That(Response(body, 3)["error"]!["message"]!.GetValue<string>()).Contains("Unknown tool ''");
        await Assert.That(Response(body, 4)["error"]!["message"]!.GetValue<string>()).Contains("Unknown tool ''");
    }

    /// <summary>
    /// Each of these is legal JSON that is not a request object this server can serve. Every one of
    /// them used to throw <see cref="InvalidOperationException"/> out of the read loop and kill the
    /// process — so the host saw the pipe close and every later call in the session failed.
    /// </summary>
    [Test]
    [Arguments("""[{"jsonrpc":"2.0","id":1,"method":"tools/list"}]""", "batch")]
    [Arguments("123", "number")]
    [Arguments("\"a string\"", "string")]
    [Arguments("true", "boolean")]
    public async Task Requests_that_are_not_objects_are_rejected_without_killing_the_server(
        string request,
        string kind)
    {
        _ = kind;
        var server = CreateToolServer();

        // The bad line is answered, and the server keeps serving the next one.
        string body = await RoundTripAsync(server, request, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        await Assert.That(Response(body, 0)["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32600);
        await Assert.That(Response(body, 1)["result"]!["tools"]).IsNotNull();
    }

    [Test]
    public async Task A_non_string_method_or_params_does_not_kill_the_server()
    {
        string body = await RoundTripAsync(CreateToolServer(),
            """{"jsonrpc":"2.0","id":30,"method":42}""",
            """{"jsonrpc":"2.0","id":31,"method":"initialize","params":"not-an-object"}""",
            """{"jsonrpc":"2.0","id":32,"method":"tools/call","params":42}""");

        await Assert.That(Response(body, 0)["error"]!["message"]!.GetValue<string>()).Contains("Unknown method");
        await Assert.That(Response(body, 1)["result"]!["protocolVersion"]!.GetValue<string>()).IsEqualTo("2025-03-26");
        await Assert.That(Response(body, 2)["error"]!["message"]!.GetValue<string>()).Contains("Unknown tool ''");
    }

    [Test]
    public async Task An_unexpected_failure_while_dispatching_costs_one_response_not_the_session()
    {
        // A tool whose schema is not valid JSON makes tools/list throw while building its reply.
        var server = new EmissaryMcpServer(new EmissaryMcpServerOptions
        {
            Tools = { new ToolDefinition("broken", "d", "{not json", (_, _) => new ValueTask<string>("x")) },
        });

        string body = await RoundTripAsync(server,
            """{"jsonrpc":"2.0","id":50,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":51,"method":"ping"}""");

        var error = Response(body, 0)["error"]!;
        await Assert.That(error["code"]!.GetValue<int>()).IsEqualTo(-32603);
        await Assert.That(error["message"]!.GetValue<string>()).Contains("tools/list");

        // Still serving.
        await Assert.That(Response(body, 1)["result"]).IsNotNull();
    }

    [Test]
    public async Task Ping_gets_an_empty_result()
    {
        // The spec says a server MUST respond to ping; silence looks like a dead server.
        string body = await RoundTripAsync(CreateToolServer(), """{"jsonrpc":"2.0","id":40,"method":"ping"}""");

        await Assert.That(Response(body)["result"]).IsNotNull();
        await Assert.That(Response(body)["error"]).IsNull();
    }

    [Test]
    public async Task Tool_exceptions_become_failed_tool_results()
    {
        var server = new EmissaryMcpServer(new EmissaryMcpServerOptions
        {
            Tools =
            {
                new ToolDefinition("boom", "d", """{"type":"object","properties":{}}""",
                    (_, _) => throw new InvalidOperationException("kaboom")),
            },
        });

        string body = await RoundTripAsync(server,
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"boom"}}""");

        // A throwing tool is a failed call, not a broken protocol, so the caller gets an isError
        // result it can reason about. The exception type is disclosed; the message is not (ADR 0007).
        var result = Response(body)["result"]!;
        await Assert.That(result["isError"]!.GetValue<bool>()).IsTrue();
        string text = result["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).IsEqualTo("Tool 'boom' failed with InvalidOperationException.");
        await Assert.That(text).DoesNotContain("kaboom");
    }

    [Test]
    public async Task Agent_tool_runs_the_whole_loop()
    {
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "echo", """{"text":"ping"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("pong"));
        var agentOptions = new AgentOptions { Tools = { SampleTools.EchoTool } };
        var server = new EmissaryMcpServer(new EmissaryMcpServerOptions
        {
            Agent = new ClaudeAgent(agentOptions, transport),
            AgentToolName = "ask",
        });

        string body = await RoundTripAsync(server,
            """{"jsonrpc":"2.0","id":9,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"ask","arguments":{"message":"go"}}}""");

        var tools = (JsonArray)Response(body, 0)["result"]!["tools"]!;
        await Assert.That(tools.Single()!["name"]!.GetValue<string>()).IsEqualTo("ask");
        var result = Response(body, 1)["result"]!;
        await Assert.That(result["isError"]!.GetValue<bool>()).IsFalse();
        await Assert.That(result["content"]![0]!["text"]!.GetValue<string>()).IsEqualTo("pong");
    }

    [Test]
    public async Task Agent_tool_requires_a_message_and_reports_abnormal_stops()
    {
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.TextTurn("partial", "max_tokens"));
        var server = new EmissaryMcpServer(new EmissaryMcpServerOptions
        {
            Agent = new ClaudeAgent(new AgentOptions(), transport),
        });

        string body = await RoundTripAsync(server,
            """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"ask_agent","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"ask_agent","arguments":{"message":"go"}}}""");

        await Assert.That(Response(body, 0)["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        var second = Response(body, 1)["result"]!;
        await Assert.That(second["isError"]!.GetValue<bool>()).IsTrue();
        await Assert.That(second["content"]![0]!["text"]!.GetValue<string>()).Contains("MaxTokens");
    }

    [Test]
    public async Task Server_validates_its_configuration()
    {
        await Assert.That(() => new EmissaryMcpServer(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => new EmissaryMcpServer(new EmissaryMcpServerOptions())).Throws<ArgumentException>();

        var server = CreateToolServer();
        await Assert.That(async () => { await server.RunAsync(null!, new MemoryStream()); })
            .Throws<ArgumentNullException>();
        await Assert.That(async () => { await server.RunAsync(new MemoryStream(), null!); })
            .Throws<ArgumentNullException>();
    }
}
