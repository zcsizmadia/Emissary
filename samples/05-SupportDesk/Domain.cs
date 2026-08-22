namespace SupportDesk;

/// <summary>A customer order as the support desk sees it.</summary>
/// <param name="OrderId">The order id, e.g. ORD-7.</param>
/// <param name="CustomerEmail">The customer's email.</param>
/// <param name="Description">What was ordered.</param>
/// <param name="Amount">The order total.</param>
/// <param name="Status">Order status: delivered, in_transit, ...</param>
/// <param name="TrackingId">The carrier tracking id, if shipped.</param>
/// <param name="Refunded">Whether a refund has been issued.</param>
public sealed record Order(
    string OrderId,
    string CustomerEmail,
    string Description,
    decimal Amount,
    string Status,
    string? TrackingId,
    bool Refunded);

/// <summary>The support desk's data access — backed by Postgres in the compose stack, in-memory for offline replay.</summary>
public interface IOrderStore
{
    Task<Order?> GetAsync(string orderId, CancellationToken cancellationToken = default);

    Task MarkRefundedAsync(string orderId, bool refunded, CancellationToken cancellationToken = default);
}
