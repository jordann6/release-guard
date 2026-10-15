namespace Orders.Api.Domain;

public sealed class InvalidOrderTransitionException : Exception
{
    public InvalidOrderTransitionException(OrderStatus from, OrderStatus to)
        : base($"An order cannot move from {from} to {to}.")
    {
        From = from;
        To = to;
    }

    public InvalidOrderTransitionException()
    {
    }

    public InvalidOrderTransitionException(string message)
        : base(message)
    {
    }

    public InvalidOrderTransitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public OrderStatus From { get; }

    public OrderStatus To { get; }
}
