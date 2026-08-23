using System.Text;
using System.Text.Json;

namespace Emissary;

/// <summary>Builds the synthetic tool that lets a model transfer a conversation to another agent.</summary>
internal static class HandoffTools
{
    /// <summary>The wire name of the transfer tool for a target, e.g. "billing" → handoff_to_billing.</summary>
    public static string ToolName(string targetName)
    {
        var builder = new StringBuilder("handoff_to_", targetName.Length + 16);
        for (int i = 0; i < targetName.Length; i++)
        {
            char c = targetName[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
        }

        return builder.ToString();
    }

    /// <summary>Reads the optional reason the model gave for transferring.</summary>
    public static string? ReasonOf(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("reason", out JsonElement reason)
            && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null;

    public static ToolDefinition Create(HandoffTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(target.Name);
        ArgumentException.ThrowIfNullOrEmpty(target.Description);
        ArgumentNullException.ThrowIfNull(target.Agent);

        return new ToolDefinition(
            ToolName(target.Name),
            $"Transfer this conversation to the {target.Name} agent, which then takes over. {target.Description}",
            """{"type":"object","properties":{"reason":{"type":"string","description":"Why this conversation is being transferred."}}}""",
            // The agent loop intercepts this call; the handler only supplies the acknowledgment
            // the model sees as the tool's result.
            (input, _) => new ValueTask<string>($"Transferring this conversation to {target.Name}."));
    }
}
