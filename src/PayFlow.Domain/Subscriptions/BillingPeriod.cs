using PayFlow.Domain.Common;
using PayFlow.Domain.Plans;

namespace PayFlow.Domain.Subscriptions;

/// <summary>
/// The half-open interval <c>[Start, End)</c> a subscription is currently paid for.
/// Half-open matters: a period ending at midnight on the 1st and the next one starting
/// at the same instant must not both contain that instant, or usage recorded exactly on
/// the boundary gets invoiced twice.
/// </summary>
public readonly record struct BillingPeriod
{
    public BillingPeriod(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            throw new DomainValidationException(nameof(end), "A billing period must end after it starts.");
        }

        Start = start;
        End = end;
    }

    public DateTimeOffset Start { get; init; }

    public DateTimeOffset End { get; init; }

    public TimeSpan Duration => End - Start;

    public static BillingPeriod Starting(DateTimeOffset start, BillingInterval interval) =>
        new(start, interval.Advance(start));

    /// <summary>True when <paramref name="instant"/> falls in <c>[Start, End)</c>.</summary>
    public bool Contains(DateTimeOffset instant) => instant >= Start && instant < End;

    public bool HasEnded(DateTimeOffset asOf) => asOf >= End;

    /// <summary>The next period of the same cadence, starting where this one ends.</summary>
    public BillingPeriod Next(BillingInterval interval) => new(End, interval.Advance(End));

    /// <summary>
    /// Fraction of the period already consumed at <paramref name="asOf"/>, clamped to 0..1.
    /// Computed from elapsed ticks rather than whole days so that a plan change at midday
    /// is not rounded to a free or fully charged day.
    /// </summary>
    public decimal ElapsedFraction(DateTimeOffset asOf)
    {
        if (asOf <= Start)
        {
            return 0m;
        }

        if (asOf >= End)
        {
            return 1m;
        }

        return (decimal)(asOf - Start).Ticks / Duration.Ticks;
    }

    /// <summary>Fraction of the period still to run — the part a mid-cycle change should credit.</summary>
    public decimal RemainingFraction(DateTimeOffset asOf) => 1m - ElapsedFraction(asOf);

    public override string ToString() => $"{Start:u} .. {End:u}";
}
