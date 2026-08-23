using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace Emissary.Transport;

/// <summary>Streams responses from the Claude API via the official Anthropic SDK.</summary>
// Thin network I/O shell over the SDK: not unit-testable without the live API. Exercised by the
// samples and the opt-in live smoke run. All pure mapping lives in AnthropicMapper (fully tested).
// ADR 0003 carve-out.
[ExcludeFromCodeCoverage]
internal sealed class AnthropicTransport : IModelTransport
{
    private readonly string? _apiKey;
    private AnthropicClient? _client;

    public AnthropicTransport(string? apiKey)
    {
        _apiKey = apiKey;
    }

    private AnthropicClient Client => _client ??= _apiKey is null
        ? new AnthropicClient()
        : new AnthropicClient { ApiKey = _apiKey };

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var parameters = AnthropicMapper.ToCreateParams(request);

        var blocks = new SortedDictionary<int, ContentBlock>();
        var textParts = new Dictionary<int, StringBuilder>();
        var thinkingParts = new Dictionary<int, (StringBuilder Text, StringBuilder Signature)>();
        var toolParts = new Dictionary<int, (string Id, string Name, StringBuilder Json)>();
        long inputTokens = 0;
        long outputTokens = 0;
        long cacheCreationTokens = 0;
        long cacheReadTokens = 0;
        string stopReason = "end_turn";

        await foreach (var streamEvent in Client.Messages.CreateStreaming(parameters, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (streamEvent.TryPickStart(out var start))
            {
                inputTokens = start.Message.Usage.InputTokens;
                cacheCreationTokens = start.Message.Usage.CacheCreationInputTokens ?? 0;
                cacheReadTokens = start.Message.Usage.CacheReadInputTokens ?? 0;
            }
            else if (streamEvent.TryPickContentBlockStart(out var blockStart))
            {
                int index = (int)blockStart.Index;
                if (blockStart.ContentBlock.TryPickText(out _))
                {
                    textParts[index] = new StringBuilder();
                }
                else if (blockStart.ContentBlock.TryPickThinking(out _))
                {
                    thinkingParts[index] = (new StringBuilder(), new StringBuilder());
                }
                else if (blockStart.ContentBlock.TryPickRedactedThinking(out var redacted))
                {
                    blocks[index] = new RedactedThinkingBlock(redacted.Data);
                }
                else if (blockStart.ContentBlock.TryPickToolUse(out var toolUse))
                {
                    toolParts[index] = (toolUse.ID, toolUse.Name, new StringBuilder());
                    yield return new StreamToolUseStart(toolUse.ID, toolUse.Name);
                }
            }
            else if (streamEvent.TryPickContentBlockDelta(out var blockDelta))
            {
                int index = (int)blockDelta.Index;

                // Guard every lookup: server-side tools (web search, code execution) stream blocks
                // whose ContentBlockStart we don't register, so their deltas must be ignored, not crash.
                if (blockDelta.Delta.TryPickText(out var text) && textParts.TryGetValue(index, out var textPart))
                {
                    textPart.Append(text.Text);
                    yield return new StreamTextDelta(text.Text);
                }
                else if (blockDelta.Delta.TryPickThinking(out var thinking) && thinkingParts.TryGetValue(index, out var thinkingDelta))
                {
                    thinkingDelta.Text.Append(thinking.Thinking);
                    yield return new StreamThinkingDelta(thinking.Thinking);
                }
                else if (blockDelta.Delta.TryPickSignature(out var signature) && thinkingParts.TryGetValue(index, out var signatureDelta))
                {
                    signatureDelta.Signature.Append(signature.Signature);
                }
                else if (blockDelta.Delta.TryPickInputJson(out var inputJson) && toolParts.TryGetValue(index, out var toolPart))
                {
                    toolPart.Json.Append(inputJson.PartialJson);
                }
            }
            else if (streamEvent.TryPickContentBlockStop(out var blockStop))
            {
                int index = (int)blockStop.Index;
                if (textParts.Remove(index, out StringBuilder? textPart))
                {
                    blocks[index] = new TextBlock(textPart.ToString());
                }
                else if (thinkingParts.Remove(index, out (StringBuilder Text, StringBuilder Signature) thinkingPart))
                {
                    string sig = thinkingPart.Signature.ToString();
                    blocks[index] = new ThinkingBlock(thinkingPart.Text.ToString(), sig.Length == 0 ? null : sig);
                }
                else if (toolParts.Remove(index, out (string Id, string Name, StringBuilder Json) toolPart))
                {
                    // A turn that hits max_tokens mid-argument still frames the stream correctly, so
                    // the accumulated JSON can be a truncated prefix. Complete what arrived instead
                    // of throwing a JsonReaderException out of the transport; the binder then
                    // reports any missing argument to the model, which can retry.
                    string json = toolPart.Json.Length == 0
                        ? "{}"
                        : PartialJson.TryComplete(toolPart.Json.ToString()) ?? "{}";
                    using var document = JsonDocument.Parse(json);
                    blocks[index] = new ToolUseBlock(toolPart.Id, toolPart.Name, document.RootElement.Clone());
                }
            }
            else if (streamEvent.TryPickDelta(out var messageDelta))
            {
                if (messageDelta.Delta.StopReason is { } reason)
                {
                    // Raw() is the unquoted wire value. ToString() renders the JSON form - quotes
                    // included - which matched nothing and silently made every run "end_turn".
                    stopReason = AnthropicMapper.NormalizeStopReason(reason.Raw());
                }

                outputTokens = messageDelta.Usage.OutputTokens;
            }
        }

        var content = blocks.Values.ToImmutableArray();

        // Authoritative: the API emits tool_use blocks iff the stop reason is tool_use. Trust the
        // assembled content over the stringified enum so a tool turn is never missed.
        if (content.Any(b => b is ToolUseBlock))
        {
            stopReason = "tool_use";
        }

        yield return new StreamCompleted(new ModelResponse(
            content, stopReason, inputTokens, outputTokens,
            cacheCreationTokens, cacheReadTokens));
    }
}
