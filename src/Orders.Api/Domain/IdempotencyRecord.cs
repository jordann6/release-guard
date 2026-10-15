namespace Orders.Api.Domain;

/// <summary>
/// Remembers which order a client's Idempotency-Key produced, together with a
/// fingerprint of the request body. The key is the primary key, so the database,
/// not application code, decides which of two concurrent requests wins.
/// </summary>
public sealed class IdempotencyRecord
{
    public const int MaxKeyLength = 128;

    private IdempotencyRecord()
    {
    }

    public IdempotencyRecord(string key, string requestFingerprint, Guid orderId, DateTimeOffset now)
    {
        Key = key;
        RequestFingerprint = requestFingerprint;
        OrderId = orderId;
        CreatedAt = now;
    }

    public string Key { get; private set; } = string.Empty;

    public string RequestFingerprint { get; private set; } = string.Empty;

    public Guid OrderId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
