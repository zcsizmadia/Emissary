using Microsoft.Data.Sqlite;

namespace Emissary.Sqlite;

/// <summary>
/// A durable <see cref="IAgentStateStore"/> backed by SQLite — suspended runs survive process
/// restarts, so a human approval can arrive days later.
/// </summary>
public sealed class SqliteAgentStateStore : IAgentStateStore
{
    private readonly string _connectionString;
    private int _initialized;

    /// <summary>Creates a store over the given SQLite connection string.</summary>
    /// <param name="connectionString">E.g. <c>"Data Source=emissary.db"</c>.</param>
    public SqliteAgentStateStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task SaveAsync(SuspendedRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO EmissarySuspensions (ConversationId, Json) VALUES ($id, $json) " +
            "ON CONFLICT(ConversationId) DO UPDATE SET Json = $json;";
        command.Parameters.AddWithValue("$id", run.ConversationId.ToString("N"));
        command.Parameters.AddWithValue("$json", run.ToJson());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SuspendedRun?> LoadAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM EmissarySuspensions WHERE ConversationId = $id;";
        command.Parameters.AddWithValue("$id", conversationId.ToString("N"));
        object? json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return json is string text ? SuspendedRun.FromJson(text) : null;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM EmissarySuspensions WHERE ConversationId = $id;";
        command.Parameters.AddWithValue("$id", conversationId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE IF NOT EXISTS EmissarySuspensions (ConversationId TEXT PRIMARY KEY, Json TEXT NOT NULL);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }
}
