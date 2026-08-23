using System.Text.Json;
using Emissary.Tests.Agents;
using Emissary.Tests.Tools;

namespace Emissary.Tests;

/// <summary>
/// A model that sends the wrong JSON type for an argument must get a repairable error result, not
/// take the whole run down with an exception out of JsonElement.
/// </summary>
public sealed class ToolArgumentValidationTests
{
    private static async Task<ToolResultBlock> CallAsync(ToolDefinition tool, string inputJson)
    {
        var options = new AgentOptions();
        options.Tools.Add(tool);
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", tool.Name, inputJson)));
        transport.EnqueueTurn(FakeTransport.TextTurn("understood"));

        var result = await new ClaudeAgent(options, transport).RunAsync("go");

        // The run completed instead of throwing, and the model saw the problem.
        await Assert.That(result.StopReason).IsEqualTo(AgentStopReason.Completed);
        return (ToolResultBlock)transport.Requests[1].Messages[^1].Content.Single();
    }

    [Test]
    [Arguments("""{"left":"one","right":2}""",
        "Tool 'add' argument 'left' must be a whole number between -2147483648 and 2147483647, but the value was the string \"one\".")]
    [Arguments("""{"left":1.5}""",
        "Tool 'add' argument 'left' must be a whole number between -2147483648 and 2147483647, but the value was the number 1.5.")]
    [Arguments("""{"left":99999999999}""",
        "Tool 'add' argument 'left' must be a whole number between -2147483648 and 2147483647, but the value was the number 99999999999.")]
    [Arguments("""{"left":true}""",
        "Tool 'add' argument 'left' must be a whole number between -2147483648 and 2147483647, but the value was true.")]
    [Arguments("""{"left":false}""",
        "Tool 'add' argument 'left' must be a whole number between -2147483648 and 2147483647, but the value was false.")]
    [Arguments("""{"left":{"n":1}}""",
        "Tool 'add' argument 'left' must be a whole number between -2147483648 and 2147483647, but the value was an object.")]
    public async Task A_wrong_typed_number_is_reported_precisely(string input, string expected)
    {
        var result = await CallAsync(SampleTools.AddTool, input);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Content).IsEqualTo(expected);
    }

    [Test]
    public async Task A_wrong_typed_string_is_reported()
    {
        var result = await CallAsync(SampleTools.EchoTool, """{"text":42}""");

        await Assert.That(result.Content).IsEqualTo(
            "Tool 'echo' argument 'text' must be a string, but the value was the number 42.");
    }

    [Test]
    public async Task A_wrong_typed_bool_is_reported()
    {
        var result = await CallAsync(SampleTools.InvertTool, """{"flag":"yes"}""");

        await Assert.That(result.Content).IsEqualTo(
            "Tool 'invert' argument 'flag' must be true or false, but the value was the string \"yes\".");
    }

    [Test]
    public async Task A_wrong_typed_double_is_reported()
    {
        var result = await CallAsync(SampleTools.SendPaymentTool, """{"amount":"ninety-nine"}""");

        await Assert.That(result.Content).IsEqualTo(
            "Tool 'send_payment' argument 'amount' must be a number, but the value was the string \"ninety-nine\".");
    }

    [Test]
    public async Task A_number_too_large_for_a_double_is_rejected_rather_than_passed_as_infinity()
    {
        var result = await CallAsync(SampleTools.SendPaymentTool, """{"amount":1e400}""");

        await Assert.That(result.Content).IsEqualTo(
            "Tool 'send_payment' argument 'amount' must be a finite number, but the value was the number 1e400.");
    }

    [Test]
    public async Task An_unknown_enum_value_lists_the_permitted_values()
    {
        var result = await CallAsync(SampleTools.RoundTripUnitTool, """{"unit":"Kelvin"}""");

        await Assert.That(result.Content).IsEqualTo(
            "Tool 'round_trip_unit' argument 'unit' must be one of: Celsius, Fahrenheit. Received \"Kelvin\".");
    }

    [Test]
    public async Task An_enum_sent_as_a_non_string_is_reported()
    {
        var result = await CallAsync(SampleTools.RoundTripUnitTool, """{"unit":0}""");

        await Assert.That(result.Content).IsEqualTo(
            "Tool 'round_trip_unit' argument 'unit' must be a string, but the value was the number 0.");
    }

    [Test]
    public async Task A_scalar_where_an_array_belongs_is_reported()
    {
        var result = await CallAsync(SampleTools.SumTool, """{"values":7}""");

        await Assert.That(result.Content).IsEqualTo(
            "Tool 'sum' argument 'values' must be an array, but the value was the number 7.");
    }

    [Test]
    public async Task A_bad_array_element_names_its_index()
    {
        var result = await CallAsync(SampleTools.JoinTool, """{"parts":["a","b",3]}""");

        await Assert.That(result.Content).IsEqualTo(
            "Tool 'join' argument 'parts' item 2 must be a string, but the value was the number 3.");
    }

    [Test]
    public async Task A_scalar_where_an_object_belongs_is_reported()
    {
        var result = await CallAsync(SampleTools.PlaceOrderTool, """{"order":"A-1"}""");

        await Assert.That(result.Content).IsEqualTo(
            "Tool 'place_order' argument 'order' must be an object, but the value was the string \"A-1\".");
    }

    [Test]
    public async Task A_bad_object_member_names_the_object_and_member()
    {
        var result = await CallAsync(SampleTools.PlaceOrderTool,
            """{"order":{"id":"A-1","address":{"city":"Ann Arbor","zip":48103},"quantity":2}}""");

        await Assert.That(result.Content).IsEqualTo(
            "Object 'Emissary.Tests.Tools.Address' member 'zip' must be a string, but the value was the number 48103.");
    }

    [Test]
    public async Task A_nested_object_sent_as_a_scalar_is_reported()
    {
        var result = await CallAsync(SampleTools.PlaceOrderTool,
            """{"order":{"id":"A-1","address":"Ann Arbor"}}""");

        await Assert.That(result.Content).IsEqualTo(
            "Object 'Emissary.Tests.Tools.Order' member 'address' must be an object, but the value was the string \"Ann Arbor\".");
    }

    [Test]
    public async Task A_bad_enum_member_of_an_object_is_reported()
    {
        var result = await CallAsync(SampleTools.ApplyPreferencesTool,
            """{"preferences":{"theme":"dark","fontSize":12,"unit":"Kelvin"}}""");

        await Assert.That(result.Content).IsEqualTo(
            "Object 'Emissary.Tests.Tools.Preferences' member 'unit' must be one of: Celsius, Fahrenheit. Received \"Kelvin\".");
    }

    [Test]
    public async Task The_model_can_correct_itself_after_a_binding_error()
    {
        var options = new AgentOptions();
        options.Tools.Add(SampleTools.AddTool);
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "add", """{"left":"two"}""")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t2", "add", """{"left":2}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("The answer is 12."));

        var result = await new ClaudeAgent(options, transport).RunAsync("add two and ten");

        var retried = (ToolResultBlock)transport.Requests[2].Messages[^1].Content.Single();
        await Assert.That(retried.IsError).IsFalse();
        await Assert.That(retried.Content).IsEqualTo("12");
        await Assert.That(result.FinalText).IsEqualTo("The answer is 12.");
    }

    [Test]
    public async Task An_oversized_value_is_quoted_but_abbreviated()
    {
        string long_ = new('z', 300);
        var result = await CallAsync(SampleTools.AddTool, $$"""{"left":"{{long_}}"}""");

        await Assert.That(result.Content).IsEqualTo(
            "Tool 'add' argument 'left' must be a whole number between -2147483648 and 2147483647, "
            + $"but the value was the string \"{new string('z', 40)}…\" (300 characters).");
    }

    [Test]
    public async Task Nulls_and_absent_arguments_keep_their_existing_meaning()
    {
        // Explicit null is treated as absent, so an optional argument falls back to its default.
        var result = await CallAsync(SampleTools.AddTool, """{"left":1,"right":null}""");
        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Content).IsEqualTo("11");

        // A required argument that is absent still reports as missing, not as mistyped.
        var missing = await CallAsync(SampleTools.AddTool, """{"right":1}""");
        await Assert.That(missing.Content).IsEqualTo("Tool 'add' is missing required argument 'left'.");
    }

    [Test]
    public async Task Describing_a_null_value_says_null()
    {
        // Not reachable through a generated binder (null is treated as absent), but the coercion
        // helpers are public and must describe every JSON kind.
        using var document = JsonDocument.Parse("""{"a":null}""");
        var value = document.RootElement.GetProperty("a");

        var thrown = Assert.Throws<ToolArgumentException>(() => ToolArguments.ReadString(value, "Argument 'a'"));
        await Assert.That(thrown!.Message).IsEqualTo("Argument 'a' must be a string, but the value was null.");
    }

    [Test]
    public async Task Every_coercion_accepts_its_own_kind()
    {
        using var document = JsonDocument.Parse(
            """{"s":"x","t":true,"f":false,"i":7,"l":9000000000,"d":1.5,"a":[1],"o":{"k":1}}""");
        var root = document.RootElement;

        await Assert.That(ToolArguments.ReadString(root.GetProperty("s"), "w")).IsEqualTo("x");
        await Assert.That(ToolArguments.ReadBool(root.GetProperty("t"), "w")).IsTrue();
        await Assert.That(ToolArguments.ReadBool(root.GetProperty("f"), "w")).IsFalse();
        await Assert.That(ToolArguments.ReadInt32(root.GetProperty("i"), "w")).IsEqualTo(7);
        await Assert.That(ToolArguments.ReadInt64(root.GetProperty("l"), "w")).IsEqualTo(9_000_000_000L);
        await Assert.That(ToolArguments.ReadDouble(root.GetProperty("d"), "w")).IsEqualTo(1.5);
        await Assert.That(ToolArguments.ReadArray(root.GetProperty("a"), "w").Count()).IsEqualTo(1);
        await Assert.That(ToolArguments.ReadObject(root.GetProperty("o"), "w").ValueKind)
            .IsEqualTo(JsonValueKind.Object);
    }

    [Test]
    public async Task Wrong_kinds_are_described_for_the_remaining_coercions()
    {
        using var document = JsonDocument.Parse("""{"s":"x","i":7,"a":[1],"big":9999999999999999999999}""");
        var root = document.RootElement;

        await Assert.That(Assert.Throws<ToolArgumentException>(
                () => ToolArguments.ReadInt64(root.GetProperty("s"), "Argument 'n'"))!.Message)
            .IsEqualTo("Argument 'n' must be a whole number, but the value was the string \"x\".");

        await Assert.That(Assert.Throws<ToolArgumentException>(
                () => ToolArguments.ReadArray(root.GetProperty("i"), "Argument 'v'"))!.Message)
            .IsEqualTo("Argument 'v' must be an array, but the value was the number 7.");

        await Assert.That(Assert.Throws<ToolArgumentException>(
                () => ToolArguments.ReadObject(root.GetProperty("a"), "Argument 'o'"))!.Message)
            .IsEqualTo("Argument 'o' must be an object, but the value was an array.");

        await Assert.That(Assert.Throws<ToolArgumentException>(
                () => ToolArguments.ReadBool(root.GetProperty("a"), "Argument 'b'"))!.Message)
            .IsEqualTo("Argument 'b' must be true or false, but the value was an array.");
    }
}
