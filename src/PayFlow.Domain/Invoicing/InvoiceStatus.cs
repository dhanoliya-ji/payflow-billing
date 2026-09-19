namespace PayFlow.Domain.Invoicing;

/// <summary>
/// Lifecycle of an invoice. Persisted as text.
/// </summary>
public enum InvoiceStatus
{
    /// <summary>Being assembled. Lines may still be added; no money is owed yet.</summary>
    Draft,

    /// <summary>Finalised and payable. Lines are frozen from here on.</summary>
    Open,

    /// <summary>Settled in full.</summary>
    Paid,

    /// <summary>Past its due date with a balance outstanding. Dunning is retrying it.</summary>
    PastDue,

    /// <summary>Dunning gave up. The balance is written off but the record is kept.</summary>
    Uncollectible,

    /// <summary>Cancelled before payment. Carries no balance and is excluded from revenue.</summary>
    Void,
}

public static class InvoiceStatusExtensions
{
    /// <summary>Statuses that still owe money and so are candidates for a payment attempt.</summary>
    public static bool IsPayable(this InvoiceStatus status) =>
        status is InvoiceStatus.Open or InvoiceStatus.PastDue;

    /// <summary>Statuses that accept no further change.</summary>
    public static bool IsClosed(this InvoiceStatus status) =>
        status is InvoiceStatus.Paid or InvoiceStatus.Void or InvoiceStatus.Uncollectible;
}
