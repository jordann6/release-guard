using Microsoft.EntityFrameworkCore;
using Orders.Api.Domain;

namespace Orders.Api.Data;

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public const string Schema = "orders";
    public const string IdempotencyKeyConstraint = "pk_idempotency_keys";

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Order>(order =>
        {
            order.ToTable("orders");
            order.HasKey(o => o.Id);
            order.Property(o => o.Id).ValueGeneratedNever();
            order.Property(o => o.Sku).HasColumnName("product_code").HasMaxLength(Order.MaxSkuLength).IsRequired();
            order.Property(o => o.CustomerReference).HasMaxLength(Order.MaxCustomerReferenceLength);
            order.Property(o => o.CancellationReason).HasMaxLength(Order.MaxCancellationReasonLength);

            // Text, not an integer: a new status never changes the meaning of an old row.
            order.Property(o => o.Status).HasConversion<string>().HasMaxLength(32).IsRequired();

            // Npgsql maps a uint row version to the xmin system column: no extra column,
            // and PostgreSQL changes it on every update.
            order.Property(o => o.Version).IsRowVersion();

            order.HasIndex(o => o.CreatedAt);
        });

        modelBuilder.Entity<IdempotencyRecord>(record =>
        {
            record.ToTable("idempotency_keys");
            record.HasKey(r => r.Key).HasName(IdempotencyKeyConstraint);
            record.Property(r => r.Key).HasMaxLength(IdempotencyRecord.MaxKeyLength);
            record.Property(r => r.RequestFingerprint).HasMaxLength(64).IsFixedLength().IsRequired();
            record.HasOne<Order>().WithMany().HasForeignKey(r => r.OrderId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
