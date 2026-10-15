namespace Orders.Api.Domain;

/// <summary>
/// Order lifecycle states. Stored as text, so new values can be added without
/// renumbering and an older release can still read every row it understands.
/// </summary>
public enum OrderStatus
{
    Received,
    Confirmed,
    Shipped,
    Delivered,
    Cancelled,
}
