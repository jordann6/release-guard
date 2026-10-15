using Orders.Api.Application;

namespace Orders.UnitTests;

public class RequestPrimitivesTests
{
    [Fact]
    public void Fingerprint_ignores_surrounding_whitespace_and_blank_references()
    {
        var a = RequestFingerprint.Compute("SKU-1", 2, null);
        var b = RequestFingerprint.Compute("  SKU-1 ", 2, "   ");

        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }

    [Theory]
    [InlineData("SKU-2", 2, null)]
    [InlineData("SKU-1", 3, null)]
    [InlineData("SKU-1", 2, "cust")]
    public void Fingerprint_changes_when_any_field_changes(string sku, int quantity, string? reference)
    {
        Assert.NotEqual(RequestFingerprint.Compute("SKU-1", 2, null), RequestFingerprint.Compute(sku, quantity, reference));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("order-2026-10-15T12:00:00Z#1", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("has space", false)]
    [InlineData("tab\tkey", false)]
    [InlineData("café", false)]
    public void Idempotency_key_format(string key, bool valid)
    {
        Assert.Equal(valid, RequestFingerprint.IsValidKey(key));
    }

    [Fact]
    public void Idempotency_key_length_is_bounded()
    {
        Assert.True(RequestFingerprint.IsValidKey(new string('k', 128)));
        Assert.False(RequestFingerprint.IsValidKey(new string('k', 129)));
    }

    [Theory]
    [InlineData("\"42\"", true, 42u)]
    [InlineData(" \"7\" ", true, 7u)]
    [InlineData("\"4294967295\"", true, uint.MaxValue)]
    [InlineData("W/\"42\"", false, 0u)]
    [InlineData("*", false, 0u)]
    [InlineData("42", false, 0u)]
    [InlineData("\"\"", false, 0u)]
    [InlineData("\"-1\"", false, 0u)]
    [InlineData("\"1\", \"2\"", false, 0u)]
    [InlineData(null, false, 0u)]
    public void Entity_tag_accepts_only_one_strong_numeric_tag(string? header, bool ok, uint expected)
    {
        Assert.Equal(ok, EntityTag.TryParse(header, out var version));
        Assert.Equal(expected, version);
    }

    [Fact]
    public void Entity_tag_round_trips()
    {
        Assert.True(EntityTag.TryParse(EntityTag.Format(123456u), out var version));
        Assert.Equal(123456u, version);
    }
}
