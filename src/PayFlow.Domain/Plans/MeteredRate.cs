using PayFlow.Domain.Common;

namespace PayFlow.Domain.Plans;

/// <summary>
/// Price for one metered dimension of a plan — "api_calls at $0.002 each, first
/// 10,000 included". A plan may meter several metrics; usage recorded against a
/// metric with no rate is invoiced at zero and flagged by the billing calculator.
/// </summary>
public sealed class MeteredRate : Entity
{
    private MeteredRate()
    {
    }

    public MeteredRate(TenantId tenantId, Guid planId, string metric, Money unitPrice, decimal includedQuantity = 0m)
        : base(tenantId)
    {
        PlanId = Guard.NotEmpty(planId);
        Metric = NormalizeMetric(metric);
        UnitPrice = unitPrice;
        IncludedQuantity = Guard.NotNegative(includedQuantity);

        if (unitPrice.IsNegative)
        {
            throw new DomainValidationException(nameof(unitPrice), "A metered rate cannot be negative.");
        }
    }

    public Guid PlanId { get; private set; }

    /// <summary>Lower-cased, trimmed metric key. Matching is exact, so the plan and the usage ingest agree on one spelling.</summary>
    public string Metric { get; private set; } = "";

    public Money UnitPrice { get; private set; }

    /// <summary>Quantity bundled into the recurring charge before per-unit pricing starts.</summary>
    public decimal IncludedQuantity { get; private set; }

    /// <summary>Prices <paramref name="quantity"/> units, discounting the included allowance.</summary>
    public Money PriceFor(decimal quantity)
    {
        var billable = Math.Max(0m, quantity - IncludedQuantity);
        return UnitPrice.Times(billable);
    }

    /// <summary>The part of <paramref name="quantity"/> that is actually charged for.</summary>
    public decimal BillableQuantity(decimal quantity) => Math.Max(0m, quantity - IncludedQuantity);

    internal static string NormalizeMetric(string metric) =>
        Guard.NotNullOrWhiteSpace(metric).ToLowerInvariant();
}
