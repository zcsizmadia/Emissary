using System.Text.Json;
using Emissary.Tests.Tools;

namespace Emissary.Tests;

public sealed class PocoToolTests
{
    private static async Task<string> Invoke(ToolDefinition tool, string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        return await tool.InvokeAsync(document.RootElement);
    }

    [Test]
    public async Task Description_falls_back_to_xml_summary()
    {
        await Assert.That(SampleTools.PlaceOrderTool.Description).IsEqualTo("Places an order.");
    }

    [Test]
    public async Task Nested_record_schema_includes_param_description()
    {
        await Assert.That(SampleTools.PlaceOrderTool.InputSchemaJson).IsEqualTo(
            """{"type":"object","properties":{"order":{"type":"object","properties":{"id":{"type":"string"},"address":{"type":"object","properties":{"city":{"type":"string"},"zip":{"type":"string"}},"required":["city","zip"]},"quantity":{"type":"integer"}},"required":["id","address"],"description":"The order to place."}},"required":["order"]}""");
    }

    [Test]
    public async Task Nested_record_binds_via_constructor()
    {
        string result = await Invoke(
            SampleTools.PlaceOrderTool,
            """{"order":{"id":"A1","address":{"city":"Oslo","zip":"0150"},"quantity":3}}""");

        await Assert.That(result).IsEqualTo("A1:Oslo/0150:3");
    }

    [Test]
    public async Task Optional_constructor_member_uses_default()
    {
        string result = await Invoke(
            SampleTools.PlaceOrderTool,
            """{"order":{"id":"A1","address":{"city":"Oslo","zip":"0150"}}}""");

        await Assert.That(result).IsEqualTo("A1:Oslo/0150:1");
    }

    [Test]
    public async Task Missing_required_nested_member_throws()
    {
        await Assert.That(async () =>
            {
                using var document = JsonDocument.Parse("""{"order":{"id":"A1","address":{"city":"Oslo"}}}""");
                await SampleTools.PlaceOrderTool.Handler(document.RootElement, default);
            })
            .Throws<ToolArgumentException>()
            .WithMessage("Object 'Emissary.Tests.Tools.Address' is missing required member 'zip'.");
    }

    [Test]
    public async Task Property_poco_marks_required_members_only()
    {
        await Assert.That(SampleTools.ApplyPreferencesTool.InputSchemaJson).IsEqualTo(
            """{"type":"object","properties":{"preferences":{"type":"object","properties":{"theme":{"type":"string"},"font_size":{"type":"integer"},"unit":{"type":"string","enum":["Celsius","Fahrenheit"]}},"required":["theme"]}},"required":["preferences"]}""");
    }

    [Test]
    public async Task Property_poco_binds_set_and_init_properties()
    {
        string result = await Invoke(
            SampleTools.ApplyPreferencesTool,
            """{"preferences":{"theme":"dark","font_size":14,"unit":"Fahrenheit"}}""");

        await Assert.That(result).IsEqualTo("dark:14:Fahrenheit");
    }

    [Test]
    public async Task Property_poco_missing_optional_members_get_defaults()
    {
        string result = await Invoke(
            SampleTools.ApplyPreferencesTool,
            """{"preferences":{"theme":"light"}}""");

        await Assert.That(result).IsEqualTo("light:0:Celsius");
    }

    [Test]
    public async Task Property_poco_missing_required_member_throws()
    {
        await Assert.That(async () =>
            {
                using var document = JsonDocument.Parse("""{"preferences":{"font_size":12}}""");
                await SampleTools.ApplyPreferencesTool.Handler(document.RootElement, default);
            })
            .Throws<ToolArgumentException>()
            .WithMessageContaining("'theme'");
    }
}
