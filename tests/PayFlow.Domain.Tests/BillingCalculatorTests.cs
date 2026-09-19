using PayFlow.Domain.Billing;
using PayFlow.Domain.Common;
using PayFlow.Domain.Invoicing;
using PayFlow.Domain.Plans;
using PayFlow.Domain.Usage;
using Xunit;

namespace PayFlow.Domain.Tests;

public sealed class BillingCalculatorTests
{
    private static readonly DateTimeOffset Jan1 = TestData.Jan1;

    private static UsageRecord Usage(Guid subscriptionId, decimal quantity, DateTimeOffset at, string metric = "api_calls") =>
        new(TestData.Tenant, subscriptionId, Guid.NewGuid(), metric, quantity, at);

    [Fact]
    public void A_flat_rate_plan_produces_one_recurring_line()
    {
        var plan = TestData.FlatPlan(amount: 29m);
        var subscription = TestData.SubscriptionOn(plan);

        var draft = BillingCalculator.Build(subscription, plan, subscription.CurrentPeriod, []);

        var line = Assert.Single(draft.Invoice.Lines);
        Assert.Equal(InvoiceLineKind.Recurring, line.Kind);
        Assert.Equal(29m, draft.Invoice.Total.Amount);
    }

    [Fact]
    public void A_per_seat_plan_shows_the_seat_count_as_the_line_quantity()
    {
        var plan = TestData.SeatPlan(perSeat: 15m);
        var subscription = TestData.SubscriptionOn(plan, quantity: 12);

        var draft = BillingCalculator.Build(subscription, plan, subscription.CurrentPeriod, []);

        var line = Assert.Single(draft.Invoice.Lines);

        // "12 x $15.00" rather than an unexplained $180.00.
        Assert.Equal(12m, line.Quantity);
        Assert.Equal(15m, line.UnitPrice.Amount);
        Assert.Equal(180m, draft.Invoice.Total.Amount);
    }

    [Fact]
    public void Metered_usage_is_priced_at_the_plan_s_rate_not_at_zero()
    {
        // The original implementation added usage lines at a hard-coded unit price of 0,
        // so every metered plan invoiced its usage for nothing.
        var plan = TestData.MeteredPlan(baseFee: 0m, unitPrice: 0.002m);
        var subscription = TestData.SubscriptionOn(plan);

        var usage = new[]
        {
            Usage(subscription.Id, 4000m, Jan1.AddDays(2)),
            Usage(subscription.Id, 6000m, Jan1.AddDays(9)),
        };

        var draft = BillingCalculator.Build(subscription, plan, subscription.CurrentPeriod, usage);

        Assert.Equal(20.00m, draft.Invoice.Total.Amount);
        Assert.Equal(2, draft.ConsumedUsage.Count);
    }

    [Fact]
    public void Usage_is_grouped_per_metric_so_the_allowance_applies_once_per_period()
    {
        var plan = TestData.MeteredPlan(unitPrice: 0.01m, included: 1000m);
        var subscription = TestData.SubscriptionOn(plan);

        var usage = new[]
        {
            Usage(subscription.Id, 600m, Jan1.AddDays(1)),
            Usage(subscription.Id, 600m, Jan1.AddDays(2)),
        };

        var draft = BillingCalculator.Build(subscription, plan, subscription.CurrentPeriod, usage);

        // 1,200 reported, 1,000 included, 200 billable. Applying the allowance per record
        // would have made both records free.
        Assert.Equal(2.00m, draft.Invoice.Total.Amount);
    }

    [Fact]
    public void Usage_entirely_inside_the_allowance_appears_on_the_invoice_at_zero()
    {
        var plan = TestData.MeteredPlan(unitPrice: 0.01m, included: 1000m);
        var subscription = TestData.SubscriptionOn(plan);

        var draft = BillingCalculator.Build(
            subscription, plan, subscription.CurrentPeriod, [Usage(subscription.Id, 400m, Jan1.AddDays(1))]);

        var line = Assert.Single(draft.Invoice.Lines);
        Assert.True(line.Amount.IsZero);
        Assert.Contains("included", line.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_for_a_metric_the_plan_does_not_price_is_shown_rather_than_dropped()
    {
        var plan = TestData.MeteredPlan(metric: "api_calls");
        var subscription = TestData.SubscriptionOn(plan);

        var draft = BillingCalculator.Build(
            subscription,
            plan,
            subscription.CurrentPeriod,
            [Usage(subscription.Id, 50m, Jan1.AddDays(1), metric: "storage_gb")]);

        var line = Assert.Single(draft.Invoice.Lines, x => x.Metric == "storage_gb");
        Assert.True(line.Amount.IsZero);
        Assert.Contains("not priced", line.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_outside_the_billed_period_is_left_for_its_own_invoice()
    {
        var plan = TestData.MeteredPlan(unitPrice: 1m);
        var subscription = TestData.SubscriptionOn(plan);

        var usage = new[]
        {
            Usage(subscription.Id, 10m, subscription.CurrentPeriodStart.AddDays(-1)),
            Usage(subscription.Id, 10m, subscription.CurrentPeriodEnd),
            Usage(subscription.Id, 10m, subscription.CurrentPeriodStart.AddDays(3)),
        };

        var draft = BillingCalculator.Build(subscription, plan, subscription.CurrentPeriod, usage);

        Assert.Equal(10m, draft.Invoice.Total.Amount);
        Assert.Single(draft.ConsumedUsage);
    }

    [Fact]
    public void Already_invoiced_usage_is_not_billed_twice()
    {
        var plan = TestData.MeteredPlan(unitPrice: 1m);
        var subscription = TestData.SubscriptionOn(plan);

        var billed = Usage(subscription.Id, 10m, Jan1.AddDays(1));
        billed.MarkInvoiced(Guid.NewGuid(), Jan1.AddDays(2));

        var draft = BillingCalculator.Build(
            subscription, plan, subscription.CurrentPeriod, [billed, Usage(subscription.Id, 5m, Jan1.AddDays(3))]);

        Assert.Equal(5m, draft.Invoice.Total.Amount);
    }

    [Fact]
    public void A_metered_plan_with_a_base_fee_produces_both_a_base_line_and_usage_lines()
    {
        var plan = TestData.MeteredPlan(baseFee: 49m, unitPrice: 0.01m);
        var subscription = TestData.SubscriptionOn(plan);

        var draft = BillingCalculator.Build(
            subscription, plan, subscription.CurrentPeriod, [Usage(subscription.Id, 500m, Jan1.AddDays(1))]);

        Assert.Equal(2, draft.Invoice.Lines.Count);
        Assert.Equal(54.00m, draft.Invoice.Total.Amount);
    }

    [Fact]
    public void A_metered_plan_with_no_base_fee_and_no_usage_produces_no_lines()
    {
        var plan = TestData.MeteredPlan(baseFee: 0m);
        var subscription = TestData.SubscriptionOn(plan);

        var draft = BillingCalculator.Build(subscription, plan, subscription.CurrentPeriod, []);

        Assert.Empty(draft.Invoice.Lines);
        Assert.Throws<DomainException>(() => draft.Invoice.Finalize(1, Jan1, TimeSpan.Zero));
    }

    [Fact]
    public void Pending_adjustments_are_attached_with_the_right_sign()
    {
        var plan = TestData.FlatPlan(amount: 300m);
        var subscription = TestData.SubscriptionOn(plan);

        var draft = BillingCalculator.Build(
            subscription,
            plan,
            subscription.CurrentPeriod,
            [],
            [
                new PendingAdjustment("Unused Starter time", new Money(-50m)),
                new PendingAdjustment("Growth for the rest of the period", new Money(150m)),
            ]);

        Assert.Equal(400m, draft.Invoice.Total.Amount);
        Assert.Contains(draft.Invoice.Lines, x => x.Kind == InvoiceLineKind.ProrationCredit);
        Assert.Contains(draft.Invoice.Lines, x => x.Kind == InvoiceLineKind.ProrationCharge);
    }

    [Fact]
    public void Billing_against_the_wrong_plan_is_refused()
    {
        var plan = TestData.FlatPlan();
        var other = TestData.FlatPlan(code: "OTHER");
        var subscription = TestData.SubscriptionOn(plan);

        Assert.Throws<DomainException>(() =>
            BillingCalculator.Build(subscription, other, subscription.CurrentPeriod, []));
    }

    [Fact]
    public void A_usage_record_cannot_be_invoiced_twice()
    {
        var record = Usage(Guid.NewGuid(), 10m, Jan1);
        record.MarkInvoiced(Guid.NewGuid(), Jan1);

        Assert.Throws<DomainException>(() => record.MarkInvoiced(Guid.NewGuid(), Jan1));
    }

    [Fact]
    public void Releasing_a_usage_record_makes_it_billable_again()
    {
        var record = Usage(Guid.NewGuid(), 10m, Jan1);
        record.MarkInvoiced(Guid.NewGuid(), Jan1);

        record.ReleaseFromInvoice();

        Assert.False(record.IsInvoiced);
        record.MarkInvoiced(Guid.NewGuid(), Jan1);
    }
}
