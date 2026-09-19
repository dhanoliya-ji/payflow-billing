using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PayFlow.Domain.Payments;

namespace PayFlow.Infrastructure.Persistence.Configurations;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Reference).HasMaxLength(200);
        builder.Property(x => x.FailureCode).HasMaxLength(60);
        builder.Property(x => x.FailureMessage).HasMaxLength(500);

        builder.ComplexProperty(x => x.Amount, b => b.MapMoney("amount"));
        builder.ComplexProperty(x => x.RefundedAmount, b => b.MapMoney("refunded_amount"));

        builder.Ignore(x => x.NetAmount);
        builder.Ignore(x => x.IsSuccessful);

        builder.HasIndex(x => x.InvoiceId);

        // The dunning funnel groups by attempt number and outcome; the decline-reason
        // breakdown filters on failure_code. Both are dashboard queries run on every load.
        builder.HasIndex(x => new { x.TenantId, x.Status, x.AttemptNumber });
        builder.HasIndex(x => new { x.TenantId, x.ProcessedAt });
    }
}
