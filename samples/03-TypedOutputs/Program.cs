using System.Text.Json.Serialization;
using Emissary;
using TypedOutputs;

var agent = new ClaudeAgent(new AgentOptions
{
    // Capped so a demo run costs a fraction of a cent, and a stuck run stops (SampleBudget).
    Model = SampleBudget.Model,
    MaxTurns = SampleBudget.MaxTurns,
    TokenBudget = SampleBudget.TokenBudget,
    SystemPrompt = "Extract structured data faithfully.",
    // The compile-time strict schema from the [ClaudeSchema] record below.
    OutputSchemaJson = TicketTriage.JsonSchema,
});

const string report =
    "My checkout page crashes with a 500 whenever I apply a discount code. " +
    "Started yesterday, blocking all purchases!";

Console.WriteLine($"> Triage this bug report:\n{report}");
Console.WriteLine();

var result = await agent.RunAsync($"Triage this bug report:\n{report}");
var triage = result.FinalAs(SampleJsonContext.Default.TicketTriage);

Console.WriteLine($"Title:    {triage.Title}");
Console.WriteLine($"Severity: {triage.Severity}");
Console.WriteLine($"Tags:     {string.Join(", ", triage.Tags)}");
return 0;

namespace TypedOutputs
{
    /// <summary>A triaged support ticket.</summary>
    /// <param name="Title">A short, specific title.</param>
    /// <param name="Severity">How urgent the issue is.</param>
    /// <param name="Tags">Relevant labels, lowercase.</param>
    [ClaudeSchema]
    public sealed partial record TicketTriage(string Title, Severity Severity, string[] Tags);

    /// <summary>Urgency of a ticket.</summary>
    public enum Severity
    {
        Low,
        Medium,
        High,
        Critical,
    }

    [JsonSerializable(typeof(TicketTriage))]
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UseStringEnumConverter = true)]
    internal sealed partial class SampleJsonContext : JsonSerializerContext;
}
