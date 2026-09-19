using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PayFlow.Infrastructure.Idempotency;
using PayFlow.Infrastructure.Outbox;

namespace PayFlow.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Payload).IsRequired();
        builder.Property(x => x.LastError).HasMaxLength(1000);

        // The dispatcher polls for undelivered messages in occurrence order. Filtering
        // the index to unprocessed rows keeps it small no matter how much history the
        // table accumulates.
        builder.HasIndex(x => new { x.ProcessedAt, x.OccurredAt })
            .HasDatabaseName("ix_outbox_messages_pending");
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Key).HasMaxLength(200).IsRequired();
        builder.Property(x => x.RequestFingerprint).HasMaxLength(64).IsRequired();

        // The claim is won by inserting this row. A unique constraint is what makes that
        // atomic across concurrent requests; a SELECT-then-INSERT would let both through.
        builder.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
    }
}

internal sealed class InvoiceSequenceConfiguration : IEntityTypeConfiguration<InvoiceSequence>
{
    public void Configure(EntityTypeBuilder<InvoiceSequence> builder)
    {
        builder.ToTable("invoice_sequences");
        builder.HasKey(x => x.TenantId);
        builder.Property(x => x.LastNumber).IsRequired();
    }
}
