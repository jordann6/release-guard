using System.Diagnostics.Metrics;
using Orders.Api.Domain;

namespace Orders.Api.Application;

internal sealed class OrdersMetrics
{
    public const string MeterName = "Orders.Api";

    private readonly Counter<long> created;
    private readonly Counter<long> replayed;
    private readonly Counter<long> keyConflicts;
    private readonly Counter<long> transitions;
    private readonly Counter<long> concurrencyConflicts;

    public OrdersMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        created = meter.CreateCounter<long>("orders.created", description: "Orders created.");
        replayed = meter.CreateCounter<long>("orders.idempotent_replays", description: "Create requests answered from an existing Idempotency-Key.");
        keyConflicts = meter.CreateCounter<long>("orders.idempotency_key_conflicts", description: "Idempotency-Key reused with a different request.");
        transitions = meter.CreateCounter<long>("orders.transitions", description: "Successful status transitions.");
        concurrencyConflicts = meter.CreateCounter<long>("orders.concurrency_conflicts", description: "Writes rejected because the client's version was stale.");
    }

    public void Created() => created.Add(1);

    public void Replayed() => replayed.Add(1);

    public void KeyConflict() => keyConflicts.Add(1);

    public void Transitioned(OrderStatus to) => transitions.Add(1, new KeyValuePair<string, object?>("orders.status", to.ToString()));

    public void ConcurrencyConflict() => concurrencyConflicts.Add(1);
}
