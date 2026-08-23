using System.Text.Json;
using Anthropic.Models.Messages;
using Emissary.Tests.Tools;
using Emissary.Transport;

namespace Emissary.Tests;

public sealed class AnthropicMapperTests
{
    private static ModelRequest Request(
        string? system = null,
        ThinkingMode thinking = ThinkingMode.Adaptive,
        EffortLevel? effort = null,
        string? outputSchemaJson = null,
        PromptCacheMode caching = PromptCacheMode.None,
        IReadOnlyList<ToolDefinition>? tools = null,
        IReadOnlyList<Emissary.Message>? messages = null) =>
        new("claude-opus-5", system, 1024, thinking, effort, outputSchemaJson, caching,
            messages ?? [Emissary.Message.User("hi")], tools ?? []);

    [Test]
    public async Task Minimal_request_omits_optional_parts()
    {
        var parameters = AnthropicMapper.ToCreateParams(Request());

        await Assert.That(parameters.System).IsNull();
        await Assert.That(parameters.Tools).IsNull();
        await Assert.That(parameters.OutputConfig).IsNull();
        await Assert.That(parameters.MaxTokens).IsEqualTo(1024);
        await Assert.That(parameters.Messages.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Full_request_maps_system_tools_and_effort()
    {
        var parameters = AnthropicMapper.ToCreateParams(Request(
            system: "Be terse.",
            thinking: ThinkingMode.Disabled,
            effort: EffortLevel.High,
            tools: [SampleTools.EchoTool]));

        await Assert.That(parameters.System).IsNotNull();
        await Assert.That(parameters.Tools!.Count).IsEqualTo(1);
        await Assert.That(parameters.OutputConfig).IsNotNull();
    }

    [Test]
    public async Task Output_schema_maps_to_json_format_without_effort()
    {
        var parameters = AnthropicMapper.ToCreateParams(Request(
            outputSchemaJson: """{"type":"object","properties":{},"additionalProperties":false}"""));

        await Assert.That(parameters.OutputConfig).IsNotNull();
        await Assert.That(parameters.OutputConfig!.Format).IsNotNull();
    }

    [Test]
    public async Task Output_schema_and_effort_combine()
    {
        var parameters = AnthropicMapper.ToCreateParams(Request(
            effort: EffortLevel.Low,
            outputSchemaJson: """{"type":"object","properties":{}}"""));

        await Assert.That(parameters.OutputConfig!.Format).IsNotNull();
        await Assert.That(parameters.OutputConfig!.Effort).IsNotNull();
    }

    [Test]
    public async Task ParseSchemaObject_clones_top_level_members()
    {
        var schema = AnthropicMapper.ParseSchemaObject(
            """{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}""");

        await Assert.That(schema.Keys).Contains("type");
        await Assert.That(schema.Keys).Contains("properties");
        await Assert.That(schema.Keys).Contains("required");
    }

    [Test]
    [Arguments(EffortLevel.Low)]
    [Arguments(EffortLevel.Medium)]
    [Arguments(EffortLevel.High)]
    [Arguments(EffortLevel.Max)]
    public async Task Every_effort_level_maps(EffortLevel level)
    {
        _ = AnthropicMapper.ToEffort(level);
        await Assert.That(AnthropicMapper.ToEffort(level).ToString()).IsNotNull();
    }

    [Test]
    public async Task Tool_schema_maps_properties_and_required()
    {
        var tool = AnthropicMapper.ToTool(SampleTools.AddTool);

        await Assert.That(tool.Name).IsEqualTo("add");
        await Assert.That(tool.InputSchema.Properties!.Keys).Contains("left");
        await Assert.That(tool.InputSchema.Properties!.Keys).Contains("right");
        await Assert.That(tool.InputSchema.Required!.Single()).IsEqualTo("left");
    }

    [Test]
    public async Task Schema_without_required_maps_to_null()
    {
        var (properties, required) = AnthropicMapper.ParseSchema("""{"type":"object","properties":{}}""");

        await Assert.That(properties.Count).IsEqualTo(0);
        await Assert.That(required).IsNull();
    }

    [Test]
    public async Task Roles_map_for_both_directions()
    {
        var user = AnthropicMapper.ToMessageParam(Emissary.Message.User("q"));
        var assistant = AnthropicMapper.ToMessageParam(
            new Emissary.Message(MessageRole.Assistant, [new Emissary.TextBlock("a")]));

        await Assert.That(user.Role).IsEqualTo(Role.User);
        await Assert.That(assistant.Role).IsEqualTo(Role.Assistant);
    }

    [Test]
    public async Task Every_content_block_kind_maps()
    {
        using var document = JsonDocument.Parse("""{"x":1}""");

        var text = AnthropicMapper.ToContentParam(new Emissary.TextBlock("t"));
        var signedThinking = AnthropicMapper.ToContentParam(new Emissary.ThinkingBlock("th", "sig"));
        var unsignedThinking = AnthropicMapper.ToContentParam(new Emissary.ThinkingBlock("th", null));
        var redacted = AnthropicMapper.ToContentParam(new Emissary.RedactedThinkingBlock("data"));
        var toolUse = AnthropicMapper.ToContentParam(
            new Emissary.ToolUseBlock("t1", "echo", document.RootElement));
        var toolResult = AnthropicMapper.ToContentParam(
            new Emissary.ToolResultBlock("t1", "boom", IsError: true));

        await Assert.That(text.Value).IsTypeOf<TextBlockParam>();
        await Assert.That(((ThinkingBlockParam)signedThinking.Value!).Signature).IsEqualTo("sig");
        await Assert.That(((ThinkingBlockParam)unsignedThinking.Value!).Signature).IsEqualTo("");
        await Assert.That(redacted.Value).IsTypeOf<RedactedThinkingBlockParam>();
        await Assert.That(((ToolUseBlockParam)toolUse.Value!).Name).IsEqualTo("echo");
        await Assert.That(((ToolResultBlockParam)toolResult.Value!).IsError == true).IsTrue();
    }

    [Test]
    [Arguments("ToolUse", "tool_use")]
    [Arguments("tool_use", "tool_use")]
    [Arguments("\"tool_use\"", "tool_use")]
    [Arguments("MaxTokens", "max_tokens")]
    [Arguments("max_tokens", "max_tokens")]
    [Arguments("\"max_tokens\"", "max_tokens")]
    [Arguments("model_context_window_exceeded", "max_tokens")]
    [Arguments("Refusal", "refusal")]
    [Arguments("\"refusal\"", "refusal")]
    [Arguments("EndTurn", "end_turn")]
    [Arguments("end_turn", "end_turn")]
    [Arguments("StopSequence", "end_turn")]
    [Arguments("pause_turn", "pause_turn")]
    [Arguments("\"pause_turn\"", "pause_turn")]
    [Arguments("something_new", "end_turn")]
    public async Task NormalizeStopReason_maps_pascal_wire_and_json_forms(string raw, string expected)
    {
        await Assert.That(AnthropicMapper.NormalizeStopReason(raw)).IsEqualTo(expected);
    }

    /// <summary>
    /// The regression guard for the bug this normalization exists to prevent: it asserts against
    /// what the <b>real SDK</b> produces from a real <c>message_delta</c> frame, not against a
    /// hand-written string. The SDK renders the enum as JSON (<c>"tool_use"</c>, quotes included),
    /// so reading <c>ToString()</c> once made every stop reason collapse to <c>end_turn</c> —
    /// leaving MaxTokens and Refusal unreachable in production while every offline test passed.
    /// </summary>
    [Test]
    [Arguments("end_turn", "end_turn")]
    [Arguments("tool_use", "tool_use")]
    [Arguments("max_tokens", "max_tokens")]
    [Arguments("refusal", "refusal")]
    [Arguments("pause_turn", "pause_turn")]
    public async Task The_stop_reason_the_sdk_deserializes_normalizes_correctly(string wire, string expected)
    {
        string frame = """
            {"type":"message_delta","delta":{"stop_reason":"WIRE","stop_sequence":null},"usage":{"output_tokens":7}}
            """.Replace("WIRE", wire, StringComparison.Ordinal);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<RawMessageDeltaEvent>(frame);
        var reason = deserialized!.Delta.StopReason;

        // How the transport reads it.
        await Assert.That(AnthropicMapper.NormalizeStopReason(reason!.Raw())).IsEqualTo(expected);

        // And the trap: the JSON rendering must normalize the same way, so a future refactor that
        // reaches for ToString() cannot silently reintroduce the bug.
        await Assert.That(AnthropicMapper.NormalizeStopReason(reason.ToString()!)).IsEqualTo(expected);
    }

    [Test]
    public async Task Web_search_adds_a_server_tool_to_the_request()
    {
        var ws = new WebSearchOptions { MaxUses = 3 };
        ws.AllowedDomains.Add("docs.claude.com");
        var request = Request(tools: [SampleTools.EchoTool]) with { WebSearch = ws };

        var parameters = AnthropicMapper.ToCreateParams(request);

        await Assert.That(parameters.Tools!.Count).IsEqualTo(2);
        await Assert.That(parameters.Tools!.Any(t => t.Value is WebSearchTool20260318)).IsTrue();
    }

    [Test]
    public async Task Web_search_is_the_only_tool_when_no_client_tools()
    {
        var request = Request() with { WebSearch = new WebSearchOptions() };

        var parameters = AnthropicMapper.ToCreateParams(request);

        await Assert.That(parameters.Tools!.Single().Value).IsTypeOf<WebSearchTool20260318>();
    }

    [Test]
    public async Task Web_search_tool_maps_domain_and_use_limits()
    {
        var ws = new WebSearchOptions { MaxUses = 5 };
        ws.BlockedDomains.Add("evil.example");

        var tool = AnthropicMapper.ToWebSearchTool(ws);

        await Assert.That(tool.MaxUses).IsEqualTo(5);
        await Assert.That(tool.BlockedDomains!).Contains("evil.example");
        await Assert.That(tool.AllowedDomains).IsNull();
    }

    [Test]
    public async Task No_web_search_leaves_tools_untouched()
    {
        var parameters = AnthropicMapper.ToCreateParams(Request());

        await Assert.That(parameters.Tools).IsNull();
    }

    [Test]
    public async Task Tool_input_dictionary_preserves_values()
    {
        using var document = JsonDocument.Parse("""{"a":1,"b":"two"}""");

        var dictionary = AnthropicMapper.ToInputDictionary(document.RootElement);

        await Assert.That(dictionary.Count).IsEqualTo(2);
        await Assert.That(dictionary["b"].GetString()).IsEqualTo("two");
    }
}
