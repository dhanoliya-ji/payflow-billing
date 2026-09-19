using PayFlow.Domain.Common;
using PayFlow.Domain.Subscriptions;

namespace PayFlow.Domain.Billing;

/// <summary>
/// What a mid-cycle change costs: credit for the time already paid for on the old
/// terms, and a charge for the remainder of the period on the new terms.
/// </summary>
/// <param name="Credit">Unused value of the old terms. Non-negative; applied as a negative invoice line.</param>
/// <param name="Charge">Cost of the new terms for the rest of the period. Non-negative.</param>
/// <param name="RemainingFraction">Share of the period the change applies to, 0..1.</param>
public readonly record struct ProrationResult(Money Credit, Money Charge, decimal RemainingFraction)
{
    /// <summary>
    /// Net effect on the next invoice. Positive means the customer owes more (an upgrade);
    /// negative means they are owed credit (a downgrade).
    /// </summary>
    public Money NetAmount => Charge - Credit;

    public bool IsUpgrade => NetAmount.IsPositive;

    public bool IsNoOp => Credit.IsZero && Charge.IsZero;
}

/// <summary>
/// Time-based proration. The only strategy PayFlow implements, chosen because it is the
/// one customers can verify from an invoice: value is spread evenly across the billing
/// period, and a change partway through splits it at that instant.
/// </summary>
public static class Proration
{
    /// <summary>
    /// Prices a switch from <paramref name="oldPeriodPrice"/> to <paramref name="newPeriodPrice"/>
    /// taking effect at <paramref name="effectiveAt"/> inside <paramref name="period"/>.
    /// <para>
    /// A change on the last day of a period produces almost no credit and almost no
    /// charge; a change on day one produces a full credit and a full charge. Changing
    /// exactly at the period boundary is a no-op here — the renewal already bills the
    /// new terms in full.
    /// </para>
    /// </summary>
    public static ProrationResult Calculate(
        BillingPeriod period,
        Money oldPeriodPrice,
        Money newPeriodPrice,
        DateTimeOffset effectiveAt)
    {
        if (!string.Equals(oldPeriodPrice.Currency, newPeriodPrice.Currency, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Cannot prorate across currencies ({oldPeriodPrice.Currency} to {newPeriodPrice.Currency}).");
        }

        var remaining = period.RemainingFraction(effectiveAt);
        if (remaining <= 0m)
        {
            return new ProrationResult(Money.Zero(oldPeriodPrice.Currency), Money.Zero(newPeriodPrice.Currency), 0m);
        }

        // Rounded here: both halves become invoice lines and must be figures the
        // customer can reconcile against the invoice.
        return new ProrationResult(
            oldPeriodPrice.Prorate(remaining).Round(),
            newPeriodPrice.Prorate(remaining).Round(),
            remaining);
    }
}
