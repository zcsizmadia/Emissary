using System.Text.Json;

namespace Emissary;

/// <summary>
/// A fully materialized tool: wire name, description, compile-time JSON Schema, and a
/// reflection-free dispatcher. Instances are normally produced by the Emissary source generator
/// from <see cref="ClaudeToolAttribute"/>-annotated methods, but can be built by hand.
/// </summary>
public sealed class ToolDefinition
{
    /// <summary>Creates a tool definition.</summary>
    /// <param name="name">The wire name of the tool.</param>
    /// <param name="description">The description shown to the model.</param>
    /// <param name="inputSchemaJson">The JSON Schema for the tool input, as a JSON string.</param>
    /// <param name="handler">The dispatcher invoked with the tool-use input.</param>
    /// <param name="requiredPolicy">Policy an <see cref="IToolAuthorizer"/> must grant, or <see langword="null"/>.</param>
    /// <param name="untrusted">Whether the tool's output taints the run (see <see cref="ClaudeToolAttribute.Untrusted"/>).</param>
    /// <param name="privileged">Whether the tool is blocked in tainted runs (see <see cref="ClaudeToolAttribute.Privileged"/>).</param>
    /// <param name="compensation">Handler that undoes this tool's effect given the original input, or <see langword="null"/>.</param>
    /// <param name="maxResultLength">Caps how much of the tool's output reaches the model; <see langword="null"/> for no cap.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public ToolDefinition(
        string name,
        string description,
        string inputSchemaJson,
        Func<JsonElement, CancellationToken, ValueTask<string>> handler,
        string? requiredPolicy = null,
        bool untrusted = false,
        bool privileged = false,
        Func<JsonElement, CancellationToken, ValueTask<string>>? compensation = null,
        int? maxResultLength = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(inputSchemaJson);
        ArgumentNullException.ThrowIfNull(handler);

        Name = name;
        Description = description;
        InputSchemaJson = inputSchemaJson;
        Handler = handler;
        RequiredPolicy = requiredPolicy;
        Untrusted = untrusted;
        Privileged = privileged;
        Compensation = compensation;
        if (maxResultLength is { } cap)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1, nameof(maxResultLength));
        }

        MaxResultLength = maxResultLength;
    }

    /// <summary>The wire name of the tool.</summary>
    public string Name { get; }

    /// <summary>The description shown to the model.</summary>
    public string Description { get; }

    /// <summary>The JSON Schema for the tool input, as a JSON string.</summary>
    public string InputSchemaJson { get; }

    /// <summary>The dispatcher invoked with the tool-use input.</summary>
    public Func<JsonElement, CancellationToken, ValueTask<string>> Handler { get; }

    /// <summary>The policy an <see cref="IToolAuthorizer"/> must grant, or <see langword="null"/> for none.</summary>
    public string? RequiredPolicy { get; }

    /// <summary>Whether a successful call taints the run with untrusted content.</summary>
    public bool Untrusted { get; }

    /// <summary>Whether the tool is blocked once the run is tainted.</summary>
    public bool Privileged { get; }

    /// <summary>Undoes this tool's effect given the original input, or <see langword="null"/>.</summary>
    public Func<JsonElement, CancellationToken, ValueTask<string>>? Compensation { get; }

    /// <summary>
    /// Caps how much of this tool's output reaches the model, guarding the context window and
    /// token budget against a tool that returns far more than expected.
    /// <see langword="null"/> means no cap.
    /// </summary>
    public int? MaxResultLength { get; }

    /// <summary>Invokes the tool with the given tool-use input.</summary>
    /// <param name="input">The tool-use input object.</param>
    /// <param name="cancellationToken">Cancels the tool execution.</param>
    /// <returns>The tool result content.</returns>
    public ValueTask<string> InvokeAsync(JsonElement input, CancellationToken cancellationToken = default) =>
        Handler(input, cancellationToken);
}
