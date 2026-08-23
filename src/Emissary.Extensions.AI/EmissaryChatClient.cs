using Microsoft.Extensions.AI;

namespace Emissary.Extensions.AI;

/// <summary>
/// Presents a <see cref="ClaudeAgent"/> as a <see cref="IChatClient"/>, so an Emissary agent —
/// with its tools, contracts, taint tracking, and budgets intact — can be consumed by any
/// Microsoft.Extensions.AI pipeline, including Microsoft Agent Framework orchestrations.
/// </summary>
/// <remarks>
/// The agent owns its own system prompt and tool loop, which is the point: those carry its
/// safety configuration. Incoming <see cref="ChatRole.System"/> and <see cref="ChatRole.Tool"/>
/// messages are therefore ignored rather than allowed to override that configuration per
/// request — configure them on <see cref="AgentOptions"/> instead.
/// </remarks>
public sealed class EmissaryChatClient : IChatClient
{
    private readonly ClaudeAgent _agent;
    private readonly ChatClientMetadata _metadata;

    /// <summary>Wraps an agent as a chat client.</summary>
    /// <param name="agent">The agent that answers each request.</param>
    /// <param name="modelId">The model id to report as metadata; defaults to Emissary's default model.</param>
    public EmissaryChatClient(ClaudeAgent agent, string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        _agent = agent;
        _metadata = new ChatClientMetadata("Emissary", providerUri: null, defaultModelId: modelId ?? EmissaryDefaults.Model);
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _agent.RunAsync(ToConversation(messages), cancellationToken).ConfigureAwait(false);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, result.FinalText))
        {
            ModelId = _metadata.DefaultModelId,
            FinishReason = ToFinishReason(result.StopReason),
            Usage = new UsageDetails
            {
                InputTokenCount = result.Usage.InputTokens,
                OutputTokenCount = result.Usage.OutputTokens,
                TotalTokenCount = result.Usage.InputTokens + result.Usage.OutputTokens,
            },
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var agentEvent in _agent.StreamAsync(ToConversation(messages), cancellationToken).ConfigureAwait(false))
        {
            switch (agentEvent)
            {
                case AgentTextEvent text:
                    yield return new ChatResponseUpdate(ChatRole.Assistant, text.Delta) { ModelId = _metadata.DefaultModelId };
                    break;
                case AgentCompletedEvent completed:
                    yield return new ChatResponseUpdate(ChatRole.Assistant, string.Empty)
                    {
                        ModelId = _metadata.DefaultModelId,
                        FinishReason = ToFinishReason(completed.Result.StopReason),
                    };
                    break;
                default:
                    break;
            }
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(ChatClientMetadata) ? _metadata
            : serviceType == typeof(ClaudeAgent) ? _agent
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    /// <summary>Nothing to release; the wrapped agent owns no disposable resources.</summary>
    public void Dispose()
    {
    }

    internal static Conversation ToConversation(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var conversation = Conversation.Start();
        foreach (var message in messages)
        {
            // Only user and assistant turns map onto an Emissary conversation; see the remarks
            // above for why system and tool messages are deliberately not honored here.
            if (message.Role == ChatRole.User)
            {
                conversation = conversation.Append(Message.User(message.Text));
            }
            else if (message.Role == ChatRole.Assistant)
            {
                conversation = conversation.Append(
                    new Message(MessageRole.Assistant, [new TextBlock(message.Text)]));
            }
        }

        return conversation;
    }

    internal static ChatFinishReason ToFinishReason(AgentStopReason stopReason) => stopReason switch
    {
        // Paused reports as Length for the same reason MaxTokens does: the response is incomplete,
        // and Length is the only value in this vocabulary that says so.
        AgentStopReason.MaxTokens or AgentStopReason.Paused => ChatFinishReason.Length,
        AgentStopReason.Refusal => ChatFinishReason.ContentFilter,
        AgentStopReason.TurnLimit or AgentStopReason.BudgetExceeded or AgentStopReason.AwaitingApproval =>
            ChatFinishReason.ToolCalls,
        _ => ChatFinishReason.Stop,
    };
}
