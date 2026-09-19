using PayFlow.Domain.Common;
using PayFlow.Domain.Invoicing;
using PayFlow.Domain.Plans;
using PayFlow.Domain.Subscriptions;
using PayFlow.Domain.Usage;

namespace PayFlow.Domain.Billing;

/// <summary>
/// Turns a subscription, its plan and its unbilled usage into a draft invoice.
/// <para>
/// This is the one place that decides what a period costs, so the API's "invoice now"
/// button and the background billing cycle cannot drift apart - which is exactly what
/// happened in the first cut, where the API priced metered usage at zero because it had
/// no access to the plan's rates.
/// </para>
/// </summary>
public static class BillingCalculator
{
    /// <summary>
    /// Builds an unfinalised invoice for <paramref name="period"/>. The caller finalises
    /// it, which assigns the invoice number and due date.
    /// </summary>
    /// <param name="subscription">The subscription being billed.</param>
    /// <param name="plan">Its current plan. Must be the plan the subscription is on.</param>
    /// <param name="period">The period to bill, normally the one that just ended.</param>
    /// <param name="usage">Unbilled usage records. Records outside the period are ignored.</param>
    /// <param name="adjustments">Pending proration credits and charges from mid-cycle changes.</param>
    public static InvoiceDraft Build(
        Subscription subscription,
        Plan plan,
        BillingPeriod period,
        IEnumerable<UsageRecord> usage,
        IEnumerable<PendingAdjustment>? adjustments = null)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(usage);

        if (plan.Id != subscription.PlanId)
        {
            throw new DomainException($"Plan {plan.Code} is not the plan subscription {subscription.Id} is on.");
        }

        var invoice = new Invoice(
            subscription.TenantId,
            subscription.CustomerId,
            period.Start,
            period.End,
            subscription.Currency,
            subscription.Id);

        AddRecurringLine(invoice, subscription, plan, period);
        var applied = AddUsageLines(invoice, plan, period, usage);
        AddAdjustmentLines(invoice, adjustments);

        return new InvoiceDraft(invoice, applied);
    }

    private static void AddRecurringLine(Invoice invoice, Subscription subscription, Plan plan, BillingPeriod period)
    {
        var description = $"{plan.Name} ({plan.Interval}) {period.Start:yyyy-MM-dd} to {period.End:yyyy-MM-dd}";

        switch (plan.PricingModel)
        {
            case PricingModel.PerSeat:
                // Priced per seat so the invoice shows "12 x $15.00" rather than an
                // unexplained $180.00 that the customer has to reverse-engineer.
                invoice.AddLine($"{description} - {subscription.Quantity} seats", subscription.Quantity, plan.Price);
                break;

            case PricingModel.FlatRate:
                invoice.AddLine(description, 1m, plan.Price);
                break;

            case PricingModel.Metered:
                // A metered plan may have a zero base fee, in which case there is no
                // recurring line at all and the invoice is pure usage.
                if (plan.Price.IsPositive)
                {
                    invoice.AddLine($"{description} - base fee", 1m, plan.Price);
                }

                break;

            default:
                throw new DomainException($"Unsupported pricing model '{plan.PricingModel}'.");
        }
    }

    private static IReadOnlyList<UsageRecord> AddUsageLines(
        Invoice invoice,
        Plan plan,
        BillingPeriod period,
        IEnumerable<UsageRecord> usage)
    {
        if (plan.PricingModel != PricingModel.Metered)
        {
            return [];
        }

        var inPeriod = usage
            .Where(x => !x.IsInvoiced && period.Contains(x.RecordedAt))
            .ToList();

        if (inPeriod.Count == 0)
        {
            return [];
        }

        // Group first, then price: the plan's included allowance applies once per metric
        // per period, not once per reported record.
        foreach (var group in inPeriod.GroupBy(x => x.Metric, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var total = group.Sum(x => x.Quantity);
            var rate = plan.RateFor(group.Key);

            if (rate is null)
            {
                // Usage for a metric the plan does not price. It is still recorded and
                // shown on the invoice at zero, so the gap is visible rather than silent.
                invoice.AddUsageLine(
                    group.Key,
                    $"{group.Key} - {total:0.####} units (not priced by {plan.Code})",
                    total,
                    Money.Zero(plan.Currency));
                continue;
            }

            var billable = rate.BillableQuantity(total);
            if (billable <= 0m)
            {
                // Entirely inside the included allowance. Shown at zero so the customer
                // can see what their allowance absorbed.
                invoice.AddUsageLine(
                    group.Key,
                    $"{group.Key} - {total:0.####} units (within {rate.IncludedQuantity:0.####} included)",
                    total,
                    Money.Zero(plan.Currency));
                continue;
            }

            var description = rate.IncludedQuantity > 0m
                ? $"{group.Key} - {billable:0.####} billable units ({rate.IncludedQuantity:0.####} included)"
                : $"{group.Key} - {billable:0.####} units";

            invoice.AddUsageLine(group.Key, description, billable, rate.UnitPrice);
        }

        return inPeriod;
    }

    private static void AddAdjustmentLines(Invoice invoice, IEnumerable<PendingAdjustment>? adjustments)
    {
        if (adjustments is null)
        {
            return;
        }

        foreach (var adjustment in adjustments)
        {
            if (adjustment.Amount.IsZero)
            {
                continue;
            }

            if (adjustment.Amount.IsNegative)
            {
                invoice.AddProrationCredit(adjustment.Description, adjustment.Amount);
            }
            else
            {
                invoice.AddProrationCharge(adjustment.Description, adjustment.Amount);
            }
        }
    }
}

/// <summary>
/// A draft invoice together with the usage records it consumed, so the caller can mark
/// those records invoiced in the same transaction that saves the invoice.
/// </summary>
/// <param name="Invoice">The unfinalised invoice.</param>
/// <param name="ConsumedUsage">Usage records billed by this invoice.</param>
public readonly record struct InvoiceDraft(Invoice Invoice, IReadOnlyList<UsageRecord> ConsumedUsage);

/// <summary>
/// A credit or charge waiting to be attached to the next invoice - the output of a
/// mid-cycle plan or seat change.
/// </summary>
/// <param name="Description">Line text shown to the customer.</param>
/// <param name="Amount">Negative for a credit, positive for a charge.</param>
public readonly record struct PendingAdjustment(string Description, Money Amount);
