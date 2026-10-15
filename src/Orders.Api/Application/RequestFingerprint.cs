using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orders.Api.Application;

/// <summary>
/// Stable hash of the meaningful fields of a create request. Two requests that would
/// create the same order produce the same fingerprint regardless of JSON formatting,
/// property order or surrounding whitespace in values.
/// </summary>
internal static class RequestFingerprint
{
    private sealed record Canonical(string Sku, int Quantity, string? CustomerReference);

    public static string Compute(string sku, int quantity, string? customerReference)
    {
        var canonical = new Canonical(
            sku.Trim(),
            quantity,
            string.IsNullOrWhiteSpace(customerReference) ? null : customerReference.Trim());

        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static bool IsValidKey(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && key.Length <= Domain.IdempotencyRecord.MaxKeyLength
        && key.All(c => c is >= '!' and <= '~');
}
