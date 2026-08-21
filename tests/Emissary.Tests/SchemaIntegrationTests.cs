using System.Text.Json;
using Emissary.Tests.Tools;

namespace Emissary.Tests;

public sealed class SchemaIntegrationTests
{
    [Test]
    public async Task Schema_is_strict_and_carries_descriptions()
    {
        await Assert.That(WeatherReport.JsonSchema).IsEqualTo(
            """{"type":"object","properties":{"city":{"type":"string","description":"The city the report covers."},"temperature_c":{"type":"number","description":"Temperature in the requested unit."},"unit":{"type":"string","enum":["Celsius","Fahrenheit"],"description":"The temperature unit."},"station":{"type":"object","properties":{"city":{"type":"string"},"zip":{"type":"string"}},"required":["city","zip"],"additionalProperties":false,"description":"The reporting station, if known."}},"required":["city","temperature_c"],"additionalProperties":false,"description":"A weather report for one city."}""");
    }

    [Test]
    public async Task Schema_is_valid_json()
    {
        using var document = JsonDocument.Parse(WeatherReport.JsonSchema);

        await Assert.That(document.RootElement.GetProperty("type").GetString()).IsEqualTo("object");
        await Assert.That(document.RootElement.GetProperty("additionalProperties").GetBoolean()).IsFalse();
    }
}
