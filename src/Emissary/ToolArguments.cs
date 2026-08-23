using System.ComponentModel;
using System.Text.Json;

namespace Emissary;

/// <summary>
/// Argument coercion for generated tool dispatchers: reads a JSON value as the parameter's declared
/// type, or throws a <see cref="ToolArgumentException"/> describing what was expected and what
/// arrived. The agent loop turns that into an error tool result, so a model that sends the wrong
/// type sees the mistake and can correct it instead of failing the run.
/// </summary>
/// <remarks>
/// Called by generated code. It is public because the generated dispatchers live in your assembly,
/// not because it is meant to be called by hand.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ToolArguments
{
    /// <summary>The longest quoted value echoed back in an error message.</summary>
    private const int MaxQuotedLength = 40;

    /// <summary>Reads a JSON string.</summary>
    /// <param name="value">The received value.</param>
    /// <param name="what">What is being bound, e.g. <c>Tool 'add' argument 'left'</c>.</param>
    /// <param name="index">The array index being bound, or -1 when binding a whole value.</param>
    public static string ReadString(JsonElement value, string what, int index = -1) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw Wrong(value, what, index, "a string");

    /// <summary>Reads a JSON boolean.</summary>
    /// <param name="value">The received value.</param>
    /// <param name="what">What is being bound.</param>
    /// <param name="index">The array index being bound, or -1 when binding a whole value.</param>
    public static bool ReadBool(JsonElement value, string what, int index = -1) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Wrong(value, what, index, "true or false"),
    };

    /// <summary>Reads a JSON number as a 32-bit integer.</summary>
    /// <param name="value">The received value.</param>
    /// <param name="what">What is being bound.</param>
    /// <param name="index">The array index being bound, or -1 when binding a whole value.</param>
    public static int ReadInt32(JsonElement value, string what, int index = -1) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : throw Wrong(value, what, index, "a whole number between -2147483648 and 2147483647");

    /// <summary>Reads a JSON number as a 64-bit integer.</summary>
    /// <param name="value">The received value.</param>
    /// <param name="what">What is being bound.</param>
    /// <param name="index">The array index being bound, or -1 when binding a whole value.</param>
    public static long ReadInt64(JsonElement value, string what, int index = -1) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number
            : throw Wrong(value, what, index, "a whole number");

    /// <summary>Reads a JSON number as a double.</summary>
    /// <param name="value">The received value.</param>
    /// <param name="what">What is being bound.</param>
    /// <param name="index">The array index being bound, or -1 when binding a whole value.</param>
    /// <remarks>
    /// A magnitude past <see cref="double.MaxValue"/> parses to infinity rather than failing, so it
    /// is rejected here — handing a tool ±∞ is worse than telling the model the number was too big.
    /// </remarks>
    public static double ReadDouble(JsonElement value, string what, int index = -1)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw Wrong(value, what, index, "a number");
        }

        double number = value.GetDouble();
        return double.IsFinite(number) ? number : throw Wrong(value, what, index, "a finite number");
    }

    /// <summary>Enumerates a JSON array.</summary>
    /// <param name="value">The received value.</param>
    /// <param name="what">What is being bound.</param>
    public static JsonElement.ArrayEnumerator ReadArray(JsonElement value, string what) =>
        value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : throw Wrong(value, what, -1, "an array");

    /// <summary>Checks that a JSON value is an object before its members are bound.</summary>
    /// <param name="value">The received value.</param>
    /// <param name="what">What is being bound.</param>
    public static JsonElement ReadObject(JsonElement value, string what) =>
        value.ValueKind == JsonValueKind.Object
            ? value
            : throw Wrong(value, what, -1, "an object");

    /// <summary>The exception for a string that is not one of an enum's values.</summary>
    /// <param name="received">The value that arrived.</param>
    /// <param name="what">What is being bound.</param>
    /// <param name="index">The array index being bound, or -1 when binding a whole value.</param>
    /// <param name="allowed">The permitted values, comma-separated.</param>
    public static ToolArgumentException Unknown(string received, string what, int index, string allowed) =>
        new($"{Where(what, index)} must be one of: {allowed}. Received {Quote(received)}.");

    private static ToolArgumentException Wrong(JsonElement value, string what, int index, string expected) =>
        new($"{Where(what, index)} must be {expected}, but the value was {Describe(value)}.");

    private static string Where(string what, int index) =>
        index < 0 ? what : $"{what} item {index}";

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => $"the string {Quote(value.GetString()!)}",
        JsonValueKind.Number => $"the number {value.GetRawText()}",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        _ => "null",
    };

    private static string Quote(string value) =>
        value.Length <= MaxQuotedLength
            ? $"\"{value}\""
            : $"\"{value[..MaxQuotedLength]}…\" ({value.Length} characters)";
}
