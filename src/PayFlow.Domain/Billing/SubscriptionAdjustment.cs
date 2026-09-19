using PayFlow.Domain.Common;

namespace PayFlow.Domain.Billing;

/// <summary>
/// A proration credit or charge produced by a mid-cycle change, parked until the next
/// invoice sweeps it up.
/// <para>
/// Mid-cycle changes are not billed on the spot. Charging a customer a $7.43 card
/// payment the moment they add a seat is hostile and expensive in transaction fees;
/// carrying the amount to the next invoice is what customers expect and what makes the
/// invoice a complete account of the period.
/// </para>
/// </summary>
public sealed class SubscriptionAdjustment : Entity
{
    private SubscriptionAdjustment()
    {
    }

    public SubscriptionAdjustment(
        TenantId tenantId,
        Guid subscriptionId,
        Guid customerId,
        string description,
        Money amount,
        DateTimeOffset effectiveAt)
        : base(tenantId)
    {
        SubscriptionId = Guard.NotEmpty(subscriptionId);
        CustomerId = Guard.NotEmpty(customerId);
        Description = Guard.NotNullOrWhiteSpace(description);
        EffectiveAt = effectiveAt;

        if (amount.IsZero)
        {
            throw new DomainValidationException(nameof(amount), "A zero adjustment has no effect and should not be stored.");
        }

        Amount = amount;
    }

    public Guid SubscriptionId { get; private set; }

    public Guid CustomerId { get; private set; }

    public string Description { get; private set; } = "";

    /// <summary>Negative for a credit to the customer, positive for an extra charge.</summary>
    public Money Amount { get; private set; }

    public DateTimeOffset EffectiveAt { get; private set; }

    /// <summary>The invoice that absorbed this adjustment, once one has. Null while pending.</summary>
    public Guid? InvoiceId { get; private set; }

    public DateTimeOffset? AppliedAt { get; private set; }

    public bool IsApplied => InvoiceId is not null;

    public bool IsCredit => Amount.IsNegative;

    public void MarkApplied(Guid invoiceId, DateTimeOffset at)
    {
        if (IsApplied)
        {
            throw new DomainException($"Adjustment {Id} was already applied to invoice {InvoiceId}.");
        }

        InvoiceId = Guard.NotEmpty(invoiceId);
        AppliedAt = at;
    }

    /// <summary>Releases the adjustment back to pending when the invoice that took it is voided.</summary>
    public void Release()
    {
        InvoiceId = null;
        AppliedAt = null;
    }

    public PendingAdjustment ToPending() => new(Description, Amount);
}
