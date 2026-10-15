using Orders.Api.Domain;

namespace Orders.UnitTests;

public class OrderLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    // Every legal move. Anything not listed here must be rejected.
    private static readonly HashSet<(OrderStatus From, OrderStatus To)> Legal =
    [
        (OrderStatus.Received, OrderStatus.Confirmed),
        (OrderStatus.Received, OrderStatus.Cancelled),
        (OrderStatus.Confirmed, OrderStatus.Shipped),
        (OrderStatus.Confirmed, OrderStatus.Cancelled),
        (OrderStatus.Shipped, OrderStatus.Delivered),
    ];

    public static TheoryData<OrderStatus, OrderStatus> AllPairs()
    {
        var data = new TheoryData<OrderStatus, OrderStatus>();
        foreach (var from in Enum.GetValues<OrderStatus>())
        {
            foreach (var to in Enum.GetValues<OrderStatus>())
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Transition_table_allows_exactly_the_legal_moves(OrderStatus from, OrderStatus to)
    {
        var order = OrderIn(from);

        if (Legal.Contains((from, to)))
        {
            order.TransitionTo(to, Now.AddMinutes(5));
            Assert.Equal(to, order.Status);
            Assert.Equal(Now.AddMinutes(5), order.UpdatedAt);
        }
        else
        {
            var ex = Assert.Throws<InvalidOrderTransitionException>(() => order.TransitionTo(to, Now));
            Assert.Equal(from, ex.From);
            Assert.Equal(to, ex.To);
            Assert.Equal(from, order.Status);
        }
    }

    [Fact]
    public void New_order_starts_received_with_trimmed_values()
    {
        var order = new Order("  SKU-1 ", 3, "  cust-9 ", Now);

        Assert.Equal(OrderStatus.Received, order.Status);
        Assert.Equal("SKU-1", order.Sku);
        Assert.Equal("cust-9", order.CustomerReference);
        Assert.Equal(Now, order.CreatedAt);
        Assert.NotEqual(Guid.Empty, order.Id);
    }

    [Fact]
    public void Blank_customer_reference_is_stored_as_null()
    {
        Assert.Null(new Order("SKU-1", 1, "   ", Now).CustomerReference);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("SKU-1", 0)]
    [InlineData("SKU-1", -4)]
    [InlineData("SKU-1", Order.MaxQuantity + 1)]
    public void Invalid_orders_are_rejected(string sku, int quantity)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Order(sku, quantity, null, Now));
    }

    [Fact]
    public void Overlong_sku_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new Order(new string('x', Order.MaxSkuLength + 1), 1, null, Now));
    }

    [Fact]
    public void Terminal_states_have_no_next_states()
    {
        Assert.Empty(Order.NextStates(OrderStatus.Delivered));
        Assert.Empty(Order.NextStates(OrderStatus.Cancelled));
    }

    private static Order OrderIn(OrderStatus status)
    {
        var order = new Order("SKU-1", 1, null, Now);
        OrderStatus[] path = status switch
        {
            OrderStatus.Received => [],
            OrderStatus.Confirmed => [OrderStatus.Confirmed],
            OrderStatus.Shipped => [OrderStatus.Confirmed, OrderStatus.Shipped],
            OrderStatus.Delivered => [OrderStatus.Confirmed, OrderStatus.Shipped, OrderStatus.Delivered],
            OrderStatus.Cancelled => [OrderStatus.Cancelled],
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

        foreach (var step in path)
        {
            order.TransitionTo(step, Now);
        }

        return order;
    }
}
