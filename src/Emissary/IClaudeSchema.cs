namespace Emissary;

/// <summary>
/// Implemented automatically by <see cref="ClaudeSchemaAttribute"/> types: exposes the
/// compile-time strict JSON Schema generically, enabling
/// <see cref="AgentOptions.WithOutput{T}"/>.
/// </summary>
public interface IClaudeSchema
{
    /// <summary>The strict JSON Schema for the implementing type, generated at compile time.</summary>
    static abstract string JsonSchema { get; }
}
