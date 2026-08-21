using System.Text.Json;
using Emissary.Tests.Tools;

namespace Emissary.Tests;

public sealed class ToolIntegrationTests
{
    private static async Task<string> Invoke(ToolDefinition tool, string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        return await tool.InvokeAsync(document.RootElement);
    }

    [Test]
    public async Task Echo_has_snake_case_name_and_exact_schema()
    {
        await Assert.That(SampleTools.EchoTool.Name).IsEqualTo("echo");
        await Assert.That(SampleTools.EchoTool.Description).IsEqualTo("Echoes the input text.");
        await Assert.That(SampleTools.EchoTool.InputSchemaJson).IsEqualTo(
            """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""");
    }

    [Test]
    public async Task Echo_round_trips_text()
    {
        await Assert.That(await Invoke(SampleTools.EchoTool, """{"text":"hello"}""")).IsEqualTo("hello");
    }

    [Test]
    public async Task Echo_missing_required_argument_throws()
    {
        await Assert.That(async () =>
            {
                using var document = JsonDocument.Parse("{}");
                await SampleTools.EchoTool.Handler(document.RootElement, default);
            })
            .Throws<ToolArgumentException>()
            .WithMessage("Tool 'echo' is missing required argument 'text'.");
    }

    [Test]
    public async Task Json_null_counts_as_missing()
    {
        await Assert.That(async () =>
            {
                using var document = JsonDocument.Parse("""{"text":null}""");
                await SampleTools.EchoTool.Handler(document.RootElement, default);
            })
            .Throws<ToolArgumentException>();
    }

    [Test]
    public async Task Null_string_result_becomes_empty()
    {
        await Assert.That(await Invoke(SampleTools.EchoOrNullTool, """{"text":""}""")).IsEqualTo("");
    }

    [Test]
    public async Task Add_applies_default_when_optional_argument_missing()
    {
        await Assert.That(await Invoke(SampleTools.AddTool, """{"left":5}""")).IsEqualTo("15");
        await Assert.That(await Invoke(SampleTools.AddTool, """{"left":5,"right":1}""")).IsEqualTo("6");
    }

    [Test]
    public async Task Add_schema_marks_only_required_parameters()
    {
        await Assert.That(SampleTools.AddTool.InputSchemaJson).IsEqualTo(
            """{"type":"object","properties":{"left":{"type":"integer"},"right":{"type":"integer"}},"required":["left"]}""");
    }

    [Test]
    public async Task GetTemperature_uses_name_override_and_enum_schema()
    {
        await Assert.That(SampleTools.GetTemperatureTool.Name).IsEqualTo("temp");
        await Assert.That(SampleTools.GetTemperatureTool.InputSchemaJson).IsEqualTo(
            """{"type":"object","properties":{"city":{"type":"string"},"unit":{"type":"string","enum":["Celsius","Fahrenheit"]}},"required":["city"]}""");
    }

    [Test]
    public async Task GetTemperature_parses_enum_and_formats_double_result()
    {
        await Assert.That(await Invoke(SampleTools.GetTemperatureTool, """{"city":"Oslo"}""")).IsEqualTo("21.5");
        await Assert.That(await Invoke(SampleTools.GetTemperatureTool, """{"city":"Oslo","unit":"Fahrenheit"}""")).IsEqualTo("70.7");
    }

    [Test]
    public async Task Unknown_enum_value_throws_tool_argument_exception()
    {
        await Assert.That(async () =>
            {
                using var document = JsonDocument.Parse("""{"unit":"Kelvin"}""");
                await SampleTools.RoundTripUnitTool.Handler(document.RootElement, default);
            })
            .Throws<ToolArgumentException>()
            .WithMessageContaining("Kelvin");
    }

    [Test]
    public async Task Enum_result_uses_member_name()
    {
        await Assert.That(await Invoke(SampleTools.RoundTripUnitTool, """{"unit":"Celsius"}""")).IsEqualTo("Celsius");
    }

    [Test]
    public async Task Sum_reads_int_array_and_returns_long()
    {
        await Assert.That(await Invoke(SampleTools.SumTool, """{"values":[1,2,3]}""")).IsEqualTo("6");
        await Assert.That(await Invoke(SampleTools.SumTool, """{"values":[1],"seed":100}""")).IsEqualTo("101");
    }

    [Test]
    public async Task Sum_schema_uses_array_items()
    {
        await Assert.That(SampleTools.SumTool.InputSchemaJson).IsEqualTo(
            """{"type":"object","properties":{"values":{"type":"array","items":{"type":"integer"}},"seed":{"type":"integer"}},"required":["values"]}""");
    }

    [Test]
    public async Task Join_reads_string_array()
    {
        await Assert.That(await Invoke(SampleTools.JoinTool, """{"parts":["a","b"],"separator":"-"}""")).IsEqualTo("a-b");
        await Assert.That(await Invoke(SampleTools.JoinTool, """{"parts":["a","b"]}""")).IsEqualTo("a,b");
    }

    [Test]
    public async Task Average_reads_double_array()
    {
        await Assert.That(await Invoke(SampleTools.AverageTool, """{"numbers":[1.5,2.5],"scale":2.0}""")).IsEqualTo("4");
    }

    [Test]
    public async Task CountTruthy_reads_bool_and_long_arrays()
    {
        await Assert.That(await Invoke(
            SampleTools.CountTruthyTool,
            """{"flags":[true,false,true],"big_values":[7,8]}""")).IsEqualTo("4");
    }

    [Test]
    public async Task Invert_formats_bool_results()
    {
        await Assert.That(await Invoke(SampleTools.InvertTool, """{"flag":true}""")).IsEqualTo("false");
        await Assert.That(await Invoke(SampleTools.InvertTool, """{"flag":false}""")).IsEqualTo("true");
    }

    [Test]
    public async Task Async_task_tool_excludes_cancellation_token_from_schema()
    {
        await Assert.That(SampleTools.EchoAsyncTool.InputSchemaJson).IsEqualTo(
            """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""");
        await Assert.That(await Invoke(SampleTools.EchoAsyncTool, """{"text":"async"}""")).IsEqualTo("async");
    }

    [Test]
    public async Task Async_valuetask_tool_returns_int_result()
    {
        await Assert.That(await Invoke(SampleTools.CountAsyncTool, """{"text":"four"}""")).IsEqualTo("4");
    }
}
