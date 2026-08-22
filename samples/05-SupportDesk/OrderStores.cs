using System.Collections.Concurrent;
using Npgsql;

namespace SupportDesk;

/// <summary>Seeded in-memory store — used for offline replay (no Postgres, no API key needed).</summary>
public sealed class InMemoryOrderStore : IOrderStore
{
    private readonly ConcurrentDictionary<string, Order> _orders = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryOrderStore()
    {
        foreach (var order in Seed.Orders)
        {
            _orders[order.OrderId] = order;
        }
    }

    public Task<Order?> GetAsync(string orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_orders.TryGetValue(orderId, out var order) ? order : null);

    public Task MarkRefundedAsync(string orderId, bool refunded, CancellationToken cancellationToken = default)
    {
        if (_orders.TryGetValue(orderId, out var order))
        {
            _orders[orderId] = order with { Refunded = refunded };
        }

        return Task.CompletedTask;
    }
}

/// <summary>Postgres-backed store — used when a connection string is configured (the compose stack).</summary>
public sealed class PostgresOrderStore : IOrderStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresOrderStore(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async Task<Order?> GetAsync(string orderId, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT order_id, customer_email, description, amount, status, tracking_id, refunded " +
            "FROM orders WHERE order_id = $1");
        command.Parameters.AddWithValue(orderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new Order(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetDecimal(3),
            reader.GetString(4),
            await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
            reader.GetBoolean(6));
    }

    public async Task MarkRefundedAsync(string orderId, bool refunded, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand("UPDATE orders SET refunded = $2 WHERE order_id = $1");
        command.Parameters.AddWithValue(orderId);
        command.Parameters.AddWithValue(refunded);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Shared seed data — mirrors db/init.sql so offline and Postgres modes behave identically.</summary>
public static class Seed
{
    public static IReadOnlyList<Order> Orders { get; } =
    [
        new("ORD-7", "dana@example.com", "Aeron office chair", 129.99m, "delivered", "TRK-7", false),
        new("ORD-9", "dana@example.com", "Standing desk mat", 45.00m, "in_transit", "TRK-9", false),
    ];
}
