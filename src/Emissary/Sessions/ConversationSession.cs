namespace Emissary;

/// <summary>
/// A durable, resumable chat session: ties a <see cref="ClaudeAgent"/> to an
/// <see cref="IConversationStore"/> by conversation id. Each turn loads the stored history,
/// runs the agent, and persists the updated conversation — so a chatbot can resume by id
/// across requests or restarts.
/// </summary>
public sealed class ConversationSession
{
    private readonly ClaudeAgent _agent;
    private readonly IConversationStore _store;

    /// <summary>Creates a session over an agent and store for a given conversation id.</summary>
    /// <param name="agent">The agent that runs each turn.</param>
    /// <param name="store">Where the conversation history is persisted.</param>
    /// <param name="id">The conversation id; a fresh one starts a new chat.</param>
    public ConversationSession(ClaudeAgent agent, IConversationStore store, ConversationId id)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(store);
        _agent = agent;
        _store = store;
        Id = id;
    }

    /// <summary>The conversation id this session reads and writes.</summary>
    public ConversationId Id { get; }

    /// <summary>Sends a user message, runs a turn on the stored history, persists it, and returns the outcome.</summary>
    /// <param name="userText">The user's message.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    public async Task<AgentResult> SendAsync(string userText, CancellationToken cancellationToken = default)
    {
        var conversation = await LoadOrStartAsync(cancellationToken).ConfigureAwait(false);
        conversation = conversation.Append(Message.User(userText));

        var result = await _agent.RunAsync(conversation, cancellationToken).ConfigureAwait(false);
        await _store.SaveAsync(result.Conversation, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Loads the stored conversation, or an empty one under this session's id.</summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    public async Task<Conversation> LoadOrStartAsync(CancellationToken cancellationToken = default) =>
        await _store.LoadAsync(Id, cancellationToken).ConfigureAwait(false)
            ?? Conversation.Restore(Id, []);
}
