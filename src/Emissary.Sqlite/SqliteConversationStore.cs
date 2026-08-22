using Emissary;
using Microsoft.Data.Sqlite;

namespace Emissary.Sqlite;

/// <summary>
/// A durable <see cref="IConversationStore"/> backed by SQLite — chat histories survive process
/// restarts, so a session can be resumed by id at any time.
/// </summary>
public sealed class SqliteConversationStore : IConversationStore
{
    private readonly string _connectionString;
    private int _initialized;

    /// <summary>Creates a store over the given SQLite connection string.</summary>
    /// <param name="connectionString">E.g. <c>"Data Source=conversations.db"</c>.</param>
    public SqliteConversationStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task SaveAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO EmissaryConversations (Id, Json) VALUES ($id, $json) " +
            "ON CONFLICT(Id) DO UPDATE SET Json = $json;";
        command.Parameters.AddWithValue("$id", conversation.Id.ToString());
        command.Parameters.AddWithValue("$json", conversation.ToJson());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Conversation?> LoadAsync(ConversationId id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM EmissaryConversations WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        object? json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return json is string text ? Conversation.FromJson(text) : null;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(ConversationId id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM EmissaryConversations WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
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
                "CREATE TABLE IF NOT EXISTS EmissaryConversations (Id TEXT PRIMARY KEY, Json TEXT NOT NULL);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }
}
