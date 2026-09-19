namespace PayFlow.Domain.Subscriptions;

/// <summary>
/// Lifecycle of a subscription. Persisted as text so the set can grow without
/// renumbering existing rows.
/// </summary>
public enum SubscriptionStatus
{
    /// <summary>In a free trial. Bills nothing; converts to <see cref="Active"/> at trial end.</summary>
    Trialing,

    /// <summary>Paid and current.</summary>
    Active,

    /// <summary>An invoice went unpaid. Still served, still retrying payment (see dunning).</summary>
    PastDue,

    /// <summary>Deliberately suspended by the tenant. Does not bill and does not accrue dunning.</summary>
    Paused,

    /// <summary>Ended by the customer or the tenant. Terminal.</summary>
    Canceled,

    /// <summary>Ended by the system — trial lapsed, or dunning exhausted. Terminal.</summary>
    Expired,
}

public static class SubscriptionStatusExtensions
{
    /// <summary>Terminal states accept no further transitions.</summary>
    public static bool IsTerminal(this SubscriptionStatus status) =>
        status is SubscriptionStatus.Canceled or SubscriptionStatus.Expired;

    /// <summary>States that generate invoices on renewal.</summary>
    public static bool IsBillable(this SubscriptionStatus status) =>
        status is SubscriptionStatus.Active or SubscriptionStatus.PastDue;

    /// <summary>States where the customer should still be served.</summary>
    public static bool IsEntitled(this SubscriptionStatus status) =>
        status is SubscriptionStatus.Trialing or SubscriptionStatus.Active or SubscriptionStatus.PastDue;
}
