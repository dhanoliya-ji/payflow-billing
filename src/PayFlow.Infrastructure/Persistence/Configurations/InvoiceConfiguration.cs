using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PayFlow.Domain.Invoicing;

namespace PayFlow.Infrastructure.Persistence.Configurations;

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();

        builder.ComplexProperty(x => x.Subtotal, b => b.MapMoney("subtotal"));
        builder.ComplexProperty(x => x.Total, b => b.MapMoney("total"));
        builder.ComplexProperty(x => x.AmountPaid, b => b.MapMoney("amount_paid"));

        // Balance and DaysOverdue are computed from stored columns, so they must not be
        // persisted as well - two representations of the same number drift.
        builder.Ignore(x => x.Balance);
        builder.Ignore(x => x.IsFullySettled);

        builder.HasMany(x => x.Lines)
            .WithOne()
            .HasForeignKey(x => x.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Invoice numbers are the tenant's audit trail; the database guarantees they are
        // unique rather than trusting the allocator.
        builder.HasIndex(x => new { x.TenantId, x.Number })
            .IsUnique()
            .HasFilter(null);

        builder.HasIndex(x => new { x.TenantId, x.CustomerId });
        builder.HasIndex(x => new { x.Status, x.DueAt });

        // Dunning's only query: payable invoices whose retry has come due.
        builder.HasIndex(x => x.NextAttemptAt).HasDatabaseName("ix_invoices_next_attempt_at");

        // Guards the double-billing check in InvoiceService.IssueAsync against a race
        // between two concurrent billing runs, which the read alone cannot.
        builder.HasIndex(x => new { x.SubscriptionId, x.PeriodStart })
            .HasDatabaseName("ix_invoices_subscription_period_start");
    }
}

internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> builder)
    {
        builder.ToTable("invoice_lines");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Description).HasMaxLength(300).IsRequired();
        builder.Property(x => x.Quantity).HasPrecision(18, 4);
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Metric).HasMaxLength(100);

        builder.ComplexProperty(x => x.UnitPrice, b => b.MapMoney("unit_price"));
        builder.ComplexProperty(x => x.Amount, b => b.MapMoney("amount"));

        builder.HasIndex(x => x.InvoiceId);
    }
}
