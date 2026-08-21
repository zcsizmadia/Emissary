using System.Collections.Concurrent;

namespace Emissary;

/// <summary>
/// Persists suspended runs between the suspension and the (possibly much later) approval.
/// Implementations decide durability: in-memory, database, blob storage.
/// </summary>
public interface IAgentStateStore
{
    /// <summary>Saves a suspended run, keyed by its conversation id.</summary>
    Task SaveAsync(SuspendedRun run, CancellationToken cancellationToken = default);

    /// <summary>Loads a suspended run, or <see langword="null"/> if none exists for the id.</summary>
    Task<SuspendedRun?> LoadAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>Removes a suspended run (after resuming it).</summary>
    Task DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default);
}

/// <summary>Process-local <see cref="IAgentStateStore"/> — suitable for tests and single-node apps.</summary>
public sealed class InMemoryAgentStateStore : IAgentStateStore
{
    private readonly ConcurrentDictionary<Guid, SuspendedRun> _runs = new();

    /// <inheritdoc />
    public Task SaveAsync(SuspendedRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        _runs[run.ConversationId] = run;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SuspendedRun?> LoadAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_runs.TryGetValue(conversationId, out var run) ? run : null);

    /// <inheritdoc />
    public Task DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        _runs.TryRemove(conversationId, out _);
        return Task.CompletedTask;
    }
}
