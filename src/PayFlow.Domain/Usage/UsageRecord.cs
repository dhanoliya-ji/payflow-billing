using PayFlow.Domain.Common;
using PayFlow.Domain.Plans;

namespace PayFlow.Domain.Usage;

/// <summary>
/// One reported quantity of a metered dimension, attributed to a subscription and a
/// point in time.
/// <para>
/// Usage is recorded against a subscription rather than a customer. The original model
/// attached it to the customer, which silently mis-billed anyone holding two metered
/// subscriptions: both invoices swept up all of that customer's usage.
/// </para>
/// <para>
/// <see cref="IdempotencyKey"/> lets a client retry a usage report safely. Metering
/// agents retry on timeouts, and a double-counted usage record becomes a double charge.
/// </para>
/// </summary>
public sealed class UsageRecord : Entity
{
    private UsageRecord()
    {
    }

    public UsageRecord(
        TenantId tenantId,
        Guid subscriptionId,
        Guid customerId,
        string metric,
        decimal quantity,
        DateTimeOffset recordedAt,
        string? idempotencyKey = null)
        : base(tenantId)
    {
        SubscriptionId = Guard.NotEmpty(subscriptionId);
        CustomerId = Guard.NotEmpty(customerId);
        Metric = MeteredRate.NormalizeMetric(metric);
        Quantity = Guard.Positive(quantity);
        RecordedAt = recordedAt;
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
    }

    public Guid SubscriptionId { get; private set; }

    public Guid CustomerId { get; private set; }

    public string Metric { get; private set; } = "";

    public decimal Quantity { get; private set; }

    /// <summary>
    /// When the usage happened, which is not when it was reported. Invoicing selects on
    /// this, so a late-arriving report for last period still lands on the right invoice
    /// provided that invoice has not been issued yet.
    /// </summary>
    public DateTimeOffset RecordedAt { get; private set; }

    /// <summary>The invoice that swept this record up, once one has. Null while unbilled.</summary>
    public Guid? InvoiceId { get; private set; }

    public DateTimeOffset? InvoicedAt { get; private set; }

    /// <summary>Client-supplied de-duplication key, unique per tenant when present.</summary>
    public string? IdempotencyKey { get; private set; }

    public bool IsInvoiced => InvoiceId is not null;

    /// <summary>
    /// Binds this record to the invoice that billed it. Throws on a second attempt:
    /// billing the same usage twice is the failure this whole class exists to prevent.
    /// </summary>
    public void MarkInvoiced(Guid invoiceId, DateTimeOffset at)
    {
        if (IsInvoiced)
        {
            throw new DomainException($"Usage record {Id} was already invoiced on {InvoicedAt:u}.");
        }

        InvoiceId = Guard.NotEmpty(invoiceId);
        InvoicedAt = at;
    }

    /// <summary>
    /// Releases the record back to unbilled, used when an invoice is voided before
    /// payment so the usage can be re-billed on a corrected invoice.
    /// </summary>
    public void ReleaseFromInvoice()
    {
        InvoiceId = null;
        InvoicedAt = null;
    }
}
