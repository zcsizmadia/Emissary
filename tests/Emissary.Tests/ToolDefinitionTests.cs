using System.Text.Json;

namespace Emissary.Tests;

public sealed class ToolDefinitionTests
{
    private static ToolDefinition Create() => new(
        "demo",
        "A demo tool.",
        """{"type":"object","properties":{}}""",
        (_, _) => new ValueTask<string>("ok"));

    [Test]
    public async Task Properties_round_trip()
    {
        var tool = Create();

        await Assert.That(tool.Name).IsEqualTo("demo");
        await Assert.That(tool.Description).IsEqualTo("A demo tool.");
        await Assert.That(tool.InputSchemaJson).IsEqualTo("""{"type":"object","properties":{}}""");
        await Assert.That((object)tool.Handler).IsNotNull();
    }

    [Test]
    public async Task InvokeAsync_delegates_to_handler()
    {
        using var document = JsonDocument.Parse("{}");

        await Assert.That(await Create().InvokeAsync(document.RootElement)).IsEqualTo("ok");
    }

    [Test]
    public async Task Null_arguments_throw()
    {
        static ValueTask<string> Handler(JsonElement input, CancellationToken token) => new("ok");

        await Assert.That(() => new ToolDefinition(null!, "d", "{}", Handler)).Throws<ArgumentNullException>();
        await Assert.That(() => new ToolDefinition("n", null!, "{}", Handler)).Throws<ArgumentNullException>();
        await Assert.That(() => new ToolDefinition("n", "d", null!, Handler)).Throws<ArgumentNullException>();
        await Assert.That(() => new ToolDefinition("n", "d", "{}", null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task ToolArgumentException_carries_message()
    {
        var exception = new ToolArgumentException("bad input");

        await Assert.That(exception.Message).IsEqualTo("bad input");
    }
}
