using PayFlow.Domain.Common;
using PayFlow.Domain.Subscriptions;

namespace PayFlow.Domain.Events;

public sealed record SubscriptionCreated(
    TenantId TenantId,
    Guid SubscriptionId,
    Guid CustomerId,
    Guid PlanId,
    SubscriptionStatus Status,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public string EventType => "subscription.created";
}

public sealed record SubscriptionRenewed(
    TenantId TenantId,
    Guid SubscriptionId,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public string EventType => "subscription.renewed";
}

public sealed record SubscriptionStatusChanged(
    TenantId TenantId,
    Guid SubscriptionId,
    SubscriptionStatus From,
    SubscriptionStatus To,
    string Reason,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public string EventType => "subscription.status_changed";
}

public sealed record SubscriptionPlanChanged(
    TenantId TenantId,
    Guid SubscriptionId,
    Guid FromPlanId,
    Guid ToPlanId,
    decimal ProratedCredit,
    decimal ProratedCharge,
    string Currency,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public string EventType => "subscription.plan_changed";
}

public sealed record SubscriptionQuantityChanged(
    TenantId TenantId,
    Guid SubscriptionId,
    int FromQuantity,
    int ToQuantity,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public string EventType => "subscription.quantity_changed";
}
