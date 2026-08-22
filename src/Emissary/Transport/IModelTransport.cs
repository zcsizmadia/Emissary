using System.Collections.Immutable;

namespace Emissary.Transport;

/// <summary>
/// The seam between the agent loop and the Claude API. Internal by design (ADR 0001): the
/// Anthropic SDK's types never cross this boundary, and Phase 3's record/replay formalizes it.
/// </summary>
internal interface IModelTransport
{
    /// <summary>Streams one model response. The stream must end with a <see cref="StreamCompleted"/>.</summary>
    IAsyncEnumerable<StreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken);
}

/// <summary>Everything the transport needs for one model call.</summary>
internal sealed record ModelRequest(
    string Model,
    string? System,
    int MaxTokens,
    ThinkingMode Thinking,
    EffortLevel? Effort,
    string? OutputSchemaJson,
    PromptCacheMode PromptCaching,
    IReadOnlyList<Message> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    WebSearchOptions? WebSearch = null);

/// <summary>One fully assembled model response.</summary>
internal sealed record ModelResponse(
    ImmutableArray<ContentBlock> Content,
    string StopReason,
    long InputTokens,
    long OutputTokens,
    long CacheCreationInputTokens = 0,
    long CacheReadInputTokens = 0);

/// <summary>An event in a transport stream.</summary>
internal abstract record StreamEvent;

/// <summary>A streamed fragment of assistant text.</summary>
internal sealed record StreamTextDelta(string Text) : StreamEvent;

/// <summary>A streamed fragment of thinking.</summary>
internal sealed record StreamThinkingDelta(string Text) : StreamEvent;

/// <summary>The model started a tool call (input still streaming).</summary>
internal sealed record StreamToolUseStart(string Id, string Name) : StreamEvent;

/// <summary>The terminal event carrying the assembled response.</summary>
internal sealed record StreamCompleted(ModelResponse Response) : StreamEvent;
