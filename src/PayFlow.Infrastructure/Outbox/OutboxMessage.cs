using PayFlow.Domain.Common;
using PayFlow.Domain.Events;

namespace PayFlow.Infrastructure.Outbox;

/// <summary>
/// A domain event queued for delivery, written in the same transaction as the state
/// change that produced it.
/// <para>
/// This is the transactional outbox pattern, and it exists because the alternative is
/// silently lossy: publish to a broker inside the transaction and a rollback leaves a
/// message claiming something that never happened; publish after committing and a crash
/// in between loses the notification entirely. Writing the message to the same database
/// makes "it happened" and "someone will be told" one atomic fact, and a dispatcher
/// delivers it afterwards with at-least-once semantics.
/// </para>
/// </summary>
public sealed class OutboxMessage
{
    private OutboxMessage()
    {
    }

    public OutboxMessage(TenantId tenantId, string eventType, string payload, DateTimeOffset occurredAt)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        EventType = eventType;
        Payload = payload;
        OccurredAt = occurredAt;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    /// <summary>Stable event name from <see cref="IDomainEvent.EventType"/>, e.g. <c>invoice.paid</c>.</summary>
    public string EventType { get; private set; } = "";

    /// <summary>JSON-serialised event body.</summary>
    public string Payload { get; private set; } = "";

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    /// <summary>Delivery attempts made. Used to back off and eventually to stop retrying.</summary>
    public int AttemptCount { get; private set; }

    public string? LastError { get; private set; }

    public bool IsProcessed => ProcessedAt is not null;

    public void MarkProcessed(DateTimeOffset at)
    {
        ProcessedAt = at;
        LastError = null;
    }

    public void MarkFailed(string error)
    {
        AttemptCount++;

        // Errors can be long; the column is bounded and the full detail belongs in logs.
        LastError = error.Length > 1000 ? error[..1000] : error;
    }
}
