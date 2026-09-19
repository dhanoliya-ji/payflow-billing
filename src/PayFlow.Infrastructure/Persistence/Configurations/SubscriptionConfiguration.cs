using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PayFlow.Domain.Billing;
using PayFlow.Domain.Subscriptions;

namespace PayFlow.Infrastructure.Persistence.Configurations;

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.ToTable("subscriptions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Interval).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();

        // The billing period is one value object over two columns rather than a pair of
        // loose dates, so "end must be after start" is enforced in one place.
        builder.ComplexProperty(x => x.CurrentPeriod, b =>
        {
            b.Property(x => x.Start).HasColumnName("current_period_start");
            b.Property(x => x.End).HasColumnName("current_period_end");
        });

        builder.HasIndex(x => new { x.TenantId, x.CustomerId });
        builder.HasIndex(x => new { x.TenantId, x.PlanId });

        builder.HasIndex(x => x.Status).HasDatabaseName("ix_subscriptions_status");

        // The billing cycle's hot query is "not terminal, and the period has ended", and
        // current_period_end is the selective half of it. EF Core cannot express an index
        // over a complex-type member, so that index is created directly in the initial
        // migration as ix_subscriptions_period_end and is not tracked in the model
        // snapshot. Anything that recreates the schema from the model alone - which
        // nothing in this repository does - would miss it.
    }
}

internal sealed class SubscriptionAdjustmentConfiguration : IEntityTypeConfiguration<SubscriptionAdjustment>
{
    public void Configure(EntityTypeBuilder<SubscriptionAdjustment> builder)
    {
        builder.ToTable("subscription_adjustments");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Description).HasMaxLength(300).IsRequired();
        builder.ComplexProperty(x => x.Amount, b => b.MapMoney("amount"));

        // Every invoice run asks for this subscription's still-unapplied adjustments.
        builder.HasIndex(x => new { x.SubscriptionId, x.InvoiceId });
    }
}
