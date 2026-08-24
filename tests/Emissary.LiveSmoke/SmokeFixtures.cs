using System.Text.Json.Serialization;

namespace Emissary.LiveSmoke;

/// <summary>A tool with an observable answer, so a live run proves the loop rather than the prose.</summary>
internal static partial class SmokeTools
{
    /// <summary>Adds two integers.</summary>
    /// <param name="left">The first number.</param>
    /// <param name="right">The second number.</param>
    [ClaudeTool]
    public static int AddNumbers(int left, int right) => left + right;
}

/// <summary>A decision, used to prove a strict output schema survives the round trip.</summary>
/// <param name="Summary">One sentence on what should happen.</param>
/// <param name="Approved">Whether the change is approved.</param>
[ClaudeSchema]
internal sealed partial record Verdict(string Summary, bool Approved);

// The generated [ClaudeSchema] names properties in snake_case, so the reader has to agree —
// otherwise every property deserializes to its default and the round trip silently produces an
// empty value rather than an error.
[JsonSerializable(typeof(Verdict))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true)]
internal sealed partial class LiveSmokeJson : JsonSerializerContext;
