using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emissary;

/// <summary>One block of message content — text, thinking, a tool call, or a tool result.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ThinkingBlock), "thinking")]
[JsonDerivedType(typeof(RedactedThinkingBlock), "redacted_thinking")]
[JsonDerivedType(typeof(ToolUseBlock), "tool_use")]
[JsonDerivedType(typeof(ToolResultBlock), "tool_result")]
public abstract record ContentBlock;

/// <summary>Plain text content.</summary>
/// <param name="Text">The text.</param>
public sealed record TextBlock(string Text) : ContentBlock;

/// <summary>Claude's internal reasoning. The signature must round-trip unmodified.</summary>
/// <param name="Thinking">The thinking text.</param>
/// <param name="Signature">The integrity signature; the API rejects tampered thinking.</param>
public sealed record ThinkingBlock(string Thinking, string? Signature) : ContentBlock;

/// <summary>Thinking withheld by the platform; must round-trip unmodified.</summary>
/// <param name="Data">The opaque payload.</param>
public sealed record RedactedThinkingBlock(string Data) : ContentBlock;

/// <summary>A tool invocation requested by the model.</summary>
/// <param name="Id">The tool-use id the result must reference.</param>
/// <param name="Name">The wire name of the tool.</param>
/// <param name="Input">The tool input object.</param>
public sealed record ToolUseBlock(string Id, string Name, JsonElement Input) : ContentBlock;

/// <summary>The result of a tool invocation, sent back to the model.</summary>
/// <param name="ToolUseId">The id of the tool use this result answers.</param>
/// <param name="Content">The result content.</param>
/// <param name="IsError">Whether the tool failed; the model can self-correct.</param>
public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError) : ContentBlock;
