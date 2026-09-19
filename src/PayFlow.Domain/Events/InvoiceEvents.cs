using PayFlow.Domain.Common;
using PayFlow.Domain.Invoicing;

namespace PayFlow.Domain.Events;

public sealed record InvoiceIssued(
    TenantId TenantId,
    Guid InvoiceId,
    Guid CustomerId,
    Guid? SubscriptionId,
    decimal Total,
    string Currency,
    DateTimeOffset DueAt,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public string EventType => "invoice.issued";
}

public sealed record InvoicePaid(
    TenantId TenantId,
    Guid InvoiceId,
    Guid CustomerId,
    decimal Total,
    string Currency,
    int AttemptsUsed,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public string EventType => "invoice.paid";
}

public sealed record InvoicePaymentFailed(
    TenantId TenantId,
    Guid InvoiceId,
    Guid CustomerId,
    int AttemptNumber,
    string FailureCode,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public string EventType => "invoice.payment_failed";
}

public sealed record InvoiceStatusChanged(
    TenantId TenantId,
    Guid InvoiceId,
    InvoiceStatus From,
    InvoiceStatus To,
    string Reason,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public string EventType => "invoice.status_changed";
}
