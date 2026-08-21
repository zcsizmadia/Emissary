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
        IReadOnlyList<ToolDefinition>? tools = null,
        IReadOnlyList<Emissary.Message>? messages = null) =>
        new("claude-opus-5", system, 1024, thinking, effort,
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
    public async Task Tool_input_dictionary_preserves_values()
    {
        using var document = JsonDocument.Parse("""{"a":1,"b":"two"}""");

        var dictionary = AnthropicMapper.ToInputDictionary(document.RootElement);

        await Assert.That(dictionary.Count).IsEqualTo(2);
        await Assert.That(dictionary["b"].GetString()).IsEqualTo("two");
    }
}
