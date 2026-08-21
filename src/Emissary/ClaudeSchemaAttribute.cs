namespace Emissary;

/// <summary>
/// Marks a partial record or class as a structured-output type. The Emissary source generator
/// adds a <c>public static string JsonSchema</c> property containing the strict JSON Schema
/// (<c>"additionalProperties": false</c> on every object level), built at compile time.
/// </summary>
/// <remarks>
/// The type (and any types it is nested in) must be declared <c>partial</c>. The type's
/// <c>&lt;summary&gt;</c> becomes the schema description and <c>&lt;param&gt;</c> tags on a
/// positional record document its members (requires <c>GenerateDocumentationFile</c>).
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class ClaudeSchemaAttribute : Attribute
{
}
