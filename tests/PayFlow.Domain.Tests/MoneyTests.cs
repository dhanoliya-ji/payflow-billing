using PayFlow.Domain.Common;
using Xunit;

namespace PayFlow.Domain.Tests;

public sealed class MoneyTests
{
    [Fact]
    public void Sub_cent_unit_prices_survive_construction()
    {
        // A metered plan priced per API call is the motivating case: rounding to two
        // decimals here would store $0.00 and bill every call for nothing.
        Assert.Equal(0.002m, new Money(0.002m).Amount);
        Assert.Equal(0.0025m, new Money(0.00254m).Amount);
    }

    [Fact]
    public void Amounts_are_stored_at_four_decimals()
    {
        Assert.Equal(10.3457m, new Money(10.34567m).Amount);
    }

    [Fact]
    public void Round_reduces_to_the_minor_units_a_customer_can_be_charged()
    {
        Assert.Equal(10.35m, new Money(10.3456m).Round().Amount);
        Assert.Equal(0.00m, new Money(0.002m).Round().Amount);
    }

    [Fact]
    public void Rounding_is_banker_s_rounding_so_repeated_halves_do_not_drift_upward()
    {
        // Away-from-zero rounding biases a long run of half-cent amounts consistently
        // upward, which over thousands of invoice lines is a real, one-directional error.
        Assert.Equal(2.34m, new Money(2.345m).Round().Amount);
        Assert.Equal(2.36m, new Money(2.355m).Round().Amount);
    }

    [Fact]
    public void Currency_is_normalised_to_upper_case()
    {
        Assert.Equal("EUR", new Money(1m, "eur").Currency);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("12A")]
    [InlineData("")]
    public void Invalid_currency_codes_are_rejected(string currency)
    {
        Assert.Throws<DomainValidationException>(() => new Money(1m, currency));
    }

    [Fact]
    public void Adding_different_currencies_throws_rather_than_silently_coercing()
    {
        var usd = new Money(10m, "USD");
        var eur = new Money(10m, "EUR");

        Assert.Throws<DomainException>(() => usd + eur);
    }

    [Fact]
    public void Comparison_across_currencies_throws()
    {
        Assert.Throws<DomainException>(() => new Money(1m, "USD") > new Money(1m, "GBP"));
    }

    [Fact]
    public void Times_multiplies_at_full_precision_before_any_rounding()
    {
        // 3 x 0.335 is 1.005 exactly. Rounding the unit price to cents first would give
        // 3 x 0.34 = 1.02, which is the error that makes per-unit pricing unusable.
        var line = new Money(0.335m).Times(3m);

        Assert.Equal(1.005m, line.Amount);
        Assert.Equal(1.00m, line.Round().Amount);
    }

    [Fact]
    public void A_realistic_metered_charge_multiplies_out_correctly()
    {
        Assert.Equal(20.00m, new Money(0.002m).Times(10_000m).Round().Amount);
    }

    [Fact]
    public void Prorate_scales_by_a_fraction_of_the_period()
    {
        Assert.Equal(25.00m, new Money(100m).Prorate(0.25m).Amount);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Prorate_rejects_fractions_outside_the_period(double fraction)
    {
        Assert.Throws<DomainValidationException>(() => new Money(100m).Prorate((decimal)fraction));
    }

    [Fact]
    public void Sum_of_an_empty_sequence_is_zero_in_the_fallback_currency()
    {
        var total = Money.Sum([], "GBP");

        Assert.True(total.IsZero);
        Assert.Equal("GBP", total.Currency);
    }

    [Fact]
    public void Sum_adds_every_element()
    {
        var total = Money.Sum([new Money(1.11m), new Money(2.22m), new Money(3.33m)]);

        Assert.Equal(6.66m, total.Amount);
    }

    [Fact]
    public void Equality_is_by_value_including_currency()
    {
        Assert.Equal(new Money(5m, "USD"), new Money(5m, "USD"));
        Assert.NotEqual(new Money(5m, "USD"), new Money(5m, "EUR"));
    }
}
