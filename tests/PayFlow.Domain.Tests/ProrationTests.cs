using PayFlow.Domain.Billing;
using PayFlow.Domain.Common;
using PayFlow.Domain.Subscriptions;
using Xunit;

namespace PayFlow.Domain.Tests;

public sealed class ProrationTests
{
    private static readonly DateTimeOffset Jan1 = TestData.Jan1;
    private static readonly BillingPeriod TenDays = new(Jan1, Jan1.AddDays(10));

    [Fact]
    public void An_upgrade_halfway_through_credits_and_charges_half_of_each_plan()
    {
        var result = Proration.Calculate(TenDays, new Money(100m), new Money(300m), Jan1.AddDays(5));

        Assert.Equal(50m, result.Credit.Amount);
        Assert.Equal(150m, result.Charge.Amount);
        Assert.Equal(100m, result.NetAmount.Amount);
        Assert.True(result.IsUpgrade);
    }

    [Fact]
    public void A_downgrade_produces_a_negative_net_the_customer_is_owed()
    {
        var result = Proration.Calculate(TenDays, new Money(300m), new Money(100m), Jan1.AddDays(5));

        Assert.Equal(-100m, result.NetAmount.Amount);
        Assert.False(result.IsUpgrade);
    }

    [Fact]
    public void A_change_on_the_first_instant_credits_and_charges_the_whole_period()
    {
        var result = Proration.Calculate(TenDays, new Money(100m), new Money(300m), Jan1);

        Assert.Equal(100m, result.Credit.Amount);
        Assert.Equal(300m, result.Charge.Amount);
    }

    [Fact]
    public void A_change_at_the_period_boundary_is_a_no_op_because_the_renewal_bills_it()
    {
        var result = Proration.Calculate(TenDays, new Money(100m), new Money(300m), TenDays.End);

        Assert.True(result.IsNoOp);
        Assert.Equal(0m, result.RemainingFraction);
    }

    [Fact]
    public void A_change_after_the_period_has_ended_is_also_a_no_op()
    {
        var result = Proration.Calculate(TenDays, new Money(100m), new Money(300m), TenDays.End.AddDays(3));

        Assert.True(result.IsNoOp);
    }

    [Fact]
    public void Proration_across_currencies_is_refused()
    {
        Assert.Throws<DomainException>(() =>
            Proration.Calculate(TenDays, new Money(100m, "USD"), new Money(100m, "EUR"), Jan1.AddDays(1)));
    }

    [Fact]
    public void Switching_plans_keeps_the_period_boundaries_so_the_anniversary_does_not_move()
    {
        var starter = TestData.FlatPlan(amount: 100m);
        var growth = TestData.FlatPlan(amount: 300m, code: "GROWTH");
        var subscription = TestData.SubscriptionOn(starter);
        var originalEnd = subscription.CurrentPeriodEnd;

        subscription.ChangePlan(starter, growth, Jan1.AddDays(15));

        Assert.Equal(growth.Id, subscription.PlanId);
        Assert.Equal(originalEnd, subscription.CurrentPeriodEnd);
    }

    [Fact]
    public void Switching_to_the_same_plan_is_refused()
    {
        var plan = TestData.FlatPlan();
        var subscription = TestData.SubscriptionOn(plan);

        Assert.Throws<DomainException>(() => subscription.ChangePlan(plan, plan, Jan1.AddDays(5)));
    }

    [Fact]
    public void Switching_to_a_plan_in_another_currency_is_refused()
    {
        var usd = TestData.FlatPlan(amount: 100m);
        var eur = TestData.FlatPlan(amount: 100m, code: "EUR_PLAN", currency: "EUR");
        var subscription = TestData.SubscriptionOn(usd);

        Assert.Throws<DomainException>(() => subscription.ChangePlan(usd, eur, Jan1.AddDays(5)));
    }

    [Fact]
    public void Adding_seats_mid_period_charges_only_for_the_time_remaining()
    {
        var plan = TestData.SeatPlan(perSeat: 30m);
        var subscription = TestData.SubscriptionOn(plan, quantity: 10);
        var periodLength = (decimal)(subscription.CurrentPeriodEnd - subscription.CurrentPeriodStart).TotalDays;
        var halfway = subscription.CurrentPeriodStart.AddDays((double)(periodLength / 2m));

        var result = subscription.ChangeQuantity(plan, 20, halfway);

        Assert.Equal(20, subscription.Quantity);

        // Credit half of 10 seats, charge half of 20 seats: net is half of 10 extra seats.
        Assert.Equal(150m, result.Credit.Amount);
        Assert.Equal(300m, result.Charge.Amount);
        Assert.Equal(150m, result.NetAmount.Amount);
    }

    [Fact]
    public void Setting_the_seat_count_to_its_current_value_changes_nothing()
    {
        var plan = TestData.SeatPlan();
        var subscription = TestData.SubscriptionOn(plan, quantity: 5);

        var result = subscription.ChangeQuantity(plan, 5, Jan1.AddDays(5));

        Assert.True(result.IsNoOp);
    }

    [Fact]
    public void Seat_counts_do_not_apply_to_flat_rate_plans()
    {
        var plan = TestData.FlatPlan();
        var subscription = TestData.SubscriptionOn(plan);

        Assert.Throws<DomainException>(() => subscription.ChangeQuantity(plan, 5, Jan1.AddDays(5)));
    }

    [Fact]
    public void A_terminal_subscription_cannot_change_plan()
    {
        var starter = TestData.FlatPlan();
        var growth = TestData.FlatPlan(code: "GROWTH", amount: 99m);
        var subscription = TestData.SubscriptionOn(starter);
        subscription.Cancel(Jan1.AddDays(1), immediately: true);

        Assert.Throws<DomainException>(() => subscription.ChangePlan(starter, growth, Jan1.AddDays(2)));
    }
}
