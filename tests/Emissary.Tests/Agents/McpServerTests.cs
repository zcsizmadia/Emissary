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
        // "null" is id-less and gets no response; the other four each get one.
        await Assert.That(lines.Length).IsEqualTo(4);
        await Assert.That(Response(body, 0)["error"]!["message"]!.GetValue<string>()).Contains("Unknown method");
        await Assert.That(Response(body, 1)["result"]!["protocolVersion"]!.GetValue<string>()).IsEqualTo("2025-03-26");
        await Assert.That(Response(body, 2)["error"]!["message"]!.GetValue<string>()).Contains("Unknown tool ''");
        await Assert.That(Response(body, 3)["error"]!["message"]!.GetValue<string>()).Contains("Unknown tool ''");
    }

    [Test]
    public async Task Tool_exceptions_become_internal_errors()
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

        var error = Response(body)["error"]!;
        await Assert.That(error["code"]!.GetValue<int>()).IsEqualTo(-32603);
        await Assert.That(error["message"]!.GetValue<string>()).IsEqualTo("kaboom");
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
