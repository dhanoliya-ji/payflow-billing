using PayFlow.Domain.Common;

namespace PayFlow.Domain.Events;

/// <summary>
/// Something that has already happened to an aggregate. Events are collected on the
/// entity during a unit of work and drained by the persistence layer into the outbox
/// table inside the same transaction, so "state changed" and "someone was told"
/// commit or roll back together.
/// </summary>
public interface IDomainEvent
{
    /// <summary>Stable name used as the outbox message type. Kept explicit so renaming a C# class does not break consumers.</summary>
    string EventType { get; }

    TenantId TenantId { get; }

    DateTimeOffset OccurredAt { get; }
}
