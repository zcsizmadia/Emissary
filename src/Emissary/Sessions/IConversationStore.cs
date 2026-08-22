using System.Collections.Concurrent;

namespace Emissary;

/// <summary>
/// Persists ongoing conversations so a chat can be resumed by id across requests or process
/// restarts. Distinct from <see cref="IAgentStateStore"/>, which persists runs paused at a
/// human-in-the-loop gate.
/// </summary>
public interface IConversationStore
{
    /// <summary>Saves (or replaces) a conversation, keyed by its id.</summary>
    Task SaveAsync(Conversation conversation, CancellationToken cancellationToken = default);

    /// <summary>Loads a conversation, or <see langword="null"/> if none exists for the id.</summary>
    Task<Conversation?> LoadAsync(ConversationId id, CancellationToken cancellationToken = default);

    /// <summary>Removes a conversation.</summary>
    Task DeleteAsync(ConversationId id, CancellationToken cancellationToken = default);
}

/// <summary>Process-local <see cref="IConversationStore"/> — suitable for tests and single-node apps.</summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<Guid, Conversation> _conversations = new();

    /// <inheritdoc />
    public Task SaveAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        _conversations[conversation.Id.Value] = conversation;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Conversation?> LoadAsync(ConversationId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_conversations.TryGetValue(id.Value, out var conversation) ? conversation : null);

    /// <inheritdoc />
    public Task DeleteAsync(ConversationId id, CancellationToken cancellationToken = default)
    {
        _conversations.TryRemove(id.Value, out _);
        return Task.CompletedTask;
    }
}
