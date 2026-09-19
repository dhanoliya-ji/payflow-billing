using PayFlow.Domain.Common;

namespace PayFlow.Domain.Plans;

/// <summary>
/// A sellable price point. Plans are immutable in the sense that matters for billing:
/// the price and cadence of a plan never change in place, because subscriptions already
/// invoiced against them would retroactively disagree with their invoices. To reprice,
/// archive the plan and publish a new one, then migrate subscriptions with
/// <c>Subscription.ChangePlan</c>, which prorates the switch.
/// </summary>
public sealed class Plan : Entity
{
    private readonly List<MeteredRate> _meteredRates = [];

    private Plan()
    {
    }

    public Plan(
        TenantId tenantId,
        string code,
        string name,
        Money price,
        BillingInterval interval,
        PricingModel pricingModel = PricingModel.FlatRate,
        int trialDays = 0)
        : base(tenantId)
    {
        Code = NormalizeCode(code);
        Name = Guard.NotNullOrWhiteSpace(name);
        Interval = interval;
        PricingModel = pricingModel;
        TrialDays = trialDays;
        Price = price;

        if (price.IsNegative)
        {
            throw new DomainValidationException(nameof(price), "Plan price cannot be negative.");
        }

        if (trialDays is < 0 or > 365)
        {
            throw new DomainValidationException(nameof(trialDays), "Trial length must be between 0 and 365 days.");
        }
    }

    /// <summary>Uppercase tenant-unique key, e.g. <c>GROWTH</c>.</summary>
    public string Code { get; private set; } = "";

    public string Name { get; private set; } = "";

    /// <summary>The recurring charge for one period. For <see cref="PricingModel.PerSeat"/> this is the per-seat price.</summary>
    public Money Price { get; private set; }

    public BillingInterval Interval { get; private set; }

    public PricingModel PricingModel { get; private set; }

    /// <summary>Free days granted at signup. Zero means the subscription starts Active.</summary>
    public int TrialDays { get; private set; }

    public bool IsActive { get; private set; } = true;

    public DateTimeOffset? ArchivedAt { get; private set; }

    public IReadOnlyList<MeteredRate> MeteredRates => _meteredRates;

    public string Currency => Price.Currency;

    /// <summary>
    /// Normalised monthly value of one unit of this plan, used for MRR. A $1,200/year
    /// plan reports $100; a $29/month plan reports $29.
    /// </summary>
    public Money MonthlyRecurringValue => new(Price.Amount * Interval.MonthlyFactor(), Price.Currency);

    public MeteredRate AddMeteredRate(string metric, Money unitPrice, decimal includedQuantity = 0m)
    {
        if (PricingModel != PricingModel.Metered)
        {
            throw new DomainException($"Plan {Code} is {PricingModel}; metered rates only apply to {nameof(PricingModel.Metered)} plans.");
        }

        if (!string.Equals(unitPrice.Currency, Price.Currency, StringComparison.Ordinal))
        {
            throw new DomainException($"Metered rate currency {unitPrice.Currency} does not match plan currency {Price.Currency}.");
        }

        var normalized = MeteredRate.NormalizeMetric(metric);
        if (_meteredRates.Any(x => string.Equals(x.Metric, normalized, StringComparison.Ordinal)))
        {
            throw new DomainException($"Plan {Code} already prices metric '{normalized}'.");
        }

        var rate = new MeteredRate(TenantId, Id, normalized, unitPrice, includedQuantity);
        _meteredRates.Add(rate);
        return rate;
    }

    public MeteredRate? RateFor(string metric)
    {
        var normalized = MeteredRate.NormalizeMetric(metric);
        return _meteredRates.SingleOrDefault(x => string.Equals(x.Metric, normalized, StringComparison.Ordinal));
    }

    /// <summary>
    /// Retires the plan from the catalogue. Existing subscriptions keep billing against
    /// it; it simply stops being offered.
    /// </summary>
    public void Archive(DateTimeOffset at)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        ArchivedAt = at;
    }

    public void Restore()
    {
        IsActive = true;
        ArchivedAt = null;
    }

    private static string NormalizeCode(string code)
    {
        var normalized = Guard.NotNullOrWhiteSpace(code).ToUpperInvariant();
        if (normalized.Length > 40 || !normalized.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        {
            throw new DomainValidationException(nameof(code), "Plan code must be up to 40 letters, digits, '-' or '_'.");
        }

        return normalized;
    }
}
