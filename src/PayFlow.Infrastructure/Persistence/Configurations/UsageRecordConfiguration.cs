using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PayFlow.Domain.Usage;

namespace PayFlow.Infrastructure.Persistence.Configurations;

internal sealed class UsageRecordConfiguration : IEntityTypeConfiguration<UsageRecord>
{
    public void Configure(EntityTypeBuilder<UsageRecord> builder)
    {
        builder.ToTable("usage_records");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Metric).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Quantity).HasPrecision(18, 4);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(200);

        // Invoicing selects unbilled usage for one subscription inside a date window;
        // this index covers exactly that predicate.
        builder.HasIndex(x => new { x.SubscriptionId, x.InvoiceId, x.RecordedAt });

        // De-duplication of retried usage reports is enforced by the database, not by a
        // read-then-write in application code that two concurrent retries can both pass.
        builder.HasIndex(x => new { x.TenantId, x.IdempotencyKey })
            .IsUnique()
            .HasFilter(null);
    }
}
