using PayFlow.Domain.Common;
using PayFlow.Domain.Plans;
using PayFlow.Domain.Subscriptions;
using Xunit;

namespace PayFlow.Domain.Tests;

public sealed class BillingPeriodTests
{
    private static readonly DateTimeOffset Jan1 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_period_must_end_after_it_starts()
    {
        Assert.Throws<DomainValidationException>(() => new BillingPeriod(Jan1, Jan1));
        Assert.Throws<DomainValidationException>(() => new BillingPeriod(Jan1, Jan1.AddDays(-1)));
    }

    [Fact]
    public void The_period_is_half_open_so_the_boundary_belongs_to_exactly_one_period()
    {
        var period = new BillingPeriod(Jan1, Jan1.AddMonths(1));

        Assert.True(period.Contains(Jan1));
        Assert.False(period.Contains(period.End));
        Assert.True(period.Next(BillingInterval.Monthly).Contains(period.End));
    }

    [Fact]
    public void Elapsed_fraction_is_clamped_outside_the_period()
    {
        var period = new BillingPeriod(Jan1, Jan1.AddDays(10));

        Assert.Equal(0m, period.ElapsedFraction(Jan1.AddDays(-5)));
        Assert.Equal(1m, period.ElapsedFraction(Jan1.AddDays(20)));
    }

    [Fact]
    public void Elapsed_fraction_is_proportional_to_time_not_whole_days()
    {
        var period = new BillingPeriod(Jan1, Jan1.AddDays(10));

        Assert.Equal(0.5m, period.ElapsedFraction(Jan1.AddDays(5)));
        Assert.Equal(0.25m, period.ElapsedFraction(Jan1.AddDays(2).AddHours(12)));
    }

    [Fact]
    public void Remaining_fraction_is_the_complement_of_elapsed()
    {
        var period = new BillingPeriod(Jan1, Jan1.AddDays(10));

        Assert.Equal(0.75m, period.RemainingFraction(Jan1.AddDays(2).AddHours(12)));
    }

    [Fact]
    public void Monthly_periods_follow_the_calendar_not_a_fixed_day_count()
    {
        // 31 Jan + 1 month clamps to 28 Feb, then to 31 Mar. Adding 30-day blocks would
        // walk the anniversary backwards a few days every year.
        var january31 = new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);
        var february = BillingPeriod.Starting(january31, BillingInterval.Monthly);

        Assert.Equal(new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero), february.End);
        Assert.Equal(new DateTimeOffset(2026, 3, 28, 0, 0, 0, TimeSpan.Zero), february.Next(BillingInterval.Monthly).End);
    }

    [Fact]
    public void A_leap_day_start_lands_on_the_29th_in_a_leap_year()
    {
        var feb29 = new DateTimeOffset(2028, 2, 29, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2029, 2, 28, 0, 0, 0, TimeSpan.Zero),
            BillingPeriod.Starting(feb29, BillingInterval.Yearly).End);
    }

    [Theory]
    [InlineData(BillingInterval.Weekly, 52)]
    [InlineData(BillingInterval.Monthly, 12)]
    [InlineData(BillingInterval.Quarterly, 4)]
    [InlineData(BillingInterval.Yearly, 1)]
    public void Periods_per_year_normalises_cadences(BillingInterval interval, int expected)
    {
        Assert.Equal(expected, interval.PeriodsPerYear());
    }

    [Fact]
    public void A_yearly_plan_contributes_a_twelfth_of_its_price_to_monthly_revenue()
    {
        Assert.Equal(1m / 12m, BillingInterval.Yearly.MonthlyFactor());
        Assert.Equal(1m, BillingInterval.Monthly.MonthlyFactor());
    }
}
