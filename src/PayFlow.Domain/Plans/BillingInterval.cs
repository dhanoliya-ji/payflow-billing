using PayFlow.Domain.Common;

namespace PayFlow.Domain.Plans;

/// <summary>How often a plan bills. Persisted as text, not as an ordinal, so adding a
/// cadence later cannot silently reinterpret existing rows.</summary>
public enum BillingInterval
{
    Weekly,
    Monthly,
    Quarterly,
    Yearly,
}

public static class BillingIntervalExtensions
{
    /// <summary>
    /// Moves <paramref name="from"/> forward by whole billing periods.
    /// <para>
    /// Calendar arithmetic, not fixed day counts: a monthly subscription starting on
    /// 31 January renews on 28 February (29 in a leap year) and then on 31 March,
    /// because <see cref="DateTimeOffset.AddMonths"/> clamps to the end of the shorter
    /// month. Adding 30-day blocks instead would drift the anniversary earlier every
    /// year, which is the classic subscription-billing bug.
    /// </para>
    /// </summary>
    public static DateTimeOffset Advance(this BillingInterval interval, DateTimeOffset from, int periods = 1) => interval switch
    {
        BillingInterval.Weekly => from.AddDays(7 * periods),
        BillingInterval.Monthly => from.AddMonths(periods),
        BillingInterval.Quarterly => from.AddMonths(3 * periods),
        BillingInterval.Yearly => from.AddYears(periods),
        _ => throw new DomainException($"Unsupported billing interval '{interval}'."),
    };

    /// <summary>
    /// How many times this interval bills in a year. Used to normalise plans of
    /// different cadences onto a single monthly recurring revenue figure.
    /// </summary>
    public static decimal PeriodsPerYear(this BillingInterval interval) => interval switch
    {
        BillingInterval.Weekly => 52m,
        BillingInterval.Monthly => 12m,
        BillingInterval.Quarterly => 4m,
        BillingInterval.Yearly => 1m,
        _ => throw new DomainException($"Unsupported billing interval '{interval}'."),
    };

    /// <summary>
    /// The share of one month that a single period of this interval represents. A yearly
    /// plan contributes one twelfth of its price to MRR; a weekly plan contributes
    /// roughly 4.33 times its price.
    /// </summary>
    public static decimal MonthlyFactor(this BillingInterval interval) =>
        interval.PeriodsPerYear() / 12m;
}
