using PayFlow.Domain.Common;
using PayFlow.Domain.Events;
using PayFlow.Domain.Plans;
using PayFlow.Domain.Subscriptions;
using Xunit;

namespace PayFlow.Domain.Tests;

public sealed class SubscriptionTests
{
    private static readonly DateTimeOffset Jan1 = TestData.Jan1;

    [Fact]
    public void A_new_subscription_starts_active_for_the_plan_s_cadence()
    {
        var plan = TestData.FlatPlan(interval: BillingInterval.Yearly);
        var subscription = TestData.SubscriptionOn(plan);

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(Jan1, subscription.CurrentPeriodStart);
        Assert.Equal(Jan1.AddYears(1), subscription.CurrentPeriodEnd);
    }

    [Fact]
    public void A_trial_subscription_s_first_period_ends_when_the_trial_does()
    {
        var plan = TestData.FlatPlan(trialDays: 14);
        var subscription = TestData.SubscriptionOn(plan, withTrial: true);

        Assert.Equal(SubscriptionStatus.Trialing, subscription.Status);
        Assert.Equal(Jan1.AddDays(14), subscription.TrialEndsAt);

        // The paid period must not overlap the trial, or the first invoice bills days the
        // customer was told were free.
        Assert.Equal(Jan1.AddDays(14), subscription.CurrentPeriodEnd);
    }

    [Fact]
    public void Requesting_a_trial_on_a_plan_that_offers_none_is_rejected()
    {
        var plan = TestData.FlatPlan(trialDays: 0);

        Assert.Throws<DomainException>(() => TestData.SubscriptionOn(plan, withTrial: true));
    }

    [Fact]
    public void Renewing_a_trial_converts_it_and_starts_the_first_paid_period_at_the_trial_end()
    {
        var plan = TestData.FlatPlan(trialDays: 14);
        var subscription = TestData.SubscriptionOn(plan, withTrial: true);
        var trialEnd = subscription.CurrentPeriodEnd;

        subscription.Renew(trialEnd);

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(trialEnd, subscription.CurrentPeriodStart);
        Assert.Equal(trialEnd.AddMonths(1), subscription.CurrentPeriodEnd);
    }

    [Fact]
    public void Renewing_before_the_period_ends_is_refused()
    {
        var subscription = TestData.SubscriptionOn(TestData.FlatPlan());

        var error = Assert.Throws<DomainException>(() => subscription.Renew(Jan1.AddDays(10)));
        Assert.Contains("has not ended", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Successive_renewals_keep_the_anniversary_on_the_calendar_day()
    {
        var subscription = TestData.SubscriptionOn(TestData.FlatPlan(), startsAt: new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero));

        subscription.Renew(subscription.CurrentPeriodEnd);
        Assert.Equal(new DateTimeOffset(2026, 3, 28, 0, 0, 0, TimeSpan.Zero), subscription.CurrentPeriodEnd);
    }

    [Fact]
    public void Past_due_recovers_to_active_and_clears_the_dunning_clock()
    {
        var subscription = TestData.SubscriptionOn(TestData.FlatPlan());

        subscription.MarkPastDue(Jan1.AddDays(1));
        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);
        Assert.Equal(1, subscription.ConsecutiveFailedPayments);
        Assert.NotNull(subscription.PastDueSince);

        subscription.MarkPaymentRecovered(Jan1.AddDays(3));
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(0, subscription.ConsecutiveFailedPayments);
        Assert.Null(subscription.PastDueSince);
    }

    [Fact]
    public void Repeated_failures_accumulate_without_re_entering_past_due()
    {
        var subscription = TestData.SubscriptionOn(TestData.FlatPlan());

        subscription.MarkPastDue(Jan1.AddDays(1));
        subscription.MarkPastDue(Jan1.AddDays(2));
        subscription.MarkPastDue(Jan1.AddDays(4));

        Assert.Equal(3, subscription.ConsecutiveFailedPayments);
        Assert.Equal(Jan1.AddDays(1), subscription.PastDueSince);
    }

    [Fact]
    public void Cancelling_defaults_to_the_end_of_the_paid_period()
    {
        var subscription = TestData.SubscriptionOn(TestData.FlatPlan());

        subscription.Cancel(Jan1.AddDays(10));

        // The customer paid for the period and keeps it; nothing is terminal yet.
        Assert.True(subscription.CancelAtPeriodEnd);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.True(subscription.IsEntitled);
    }

    [Fact]
    public void A_pending_cancellation_takes_effect_at_the_next_renewal()
    {
        var subscription = TestData.SubscriptionOn(TestData.FlatPlan());
        subscription.Cancel(Jan1.AddDays(10));

        subscription.Renew(subscription.CurrentPeriodEnd);

        Assert.Equal(SubscriptionStatus.Canceled, subscription.Status);
        Assert.NotNull(subscription.EndedAt);
    }

    [Fact]
    public void A_pending_cancellation_can_be_withdrawn_before_the_period_ends()
    {
        var subscription = TestData.SubscriptionOn(TestData.FlatPlan());
        subscription.Cancel(Jan1.AddDays(10));

        subscription.ResumeCancellation(Jan1.AddDays(12));

        Assert.False(subscription.CancelAtPeriodEnd);
        Assert.Null(subscription.CanceledAt);

        subscription.Renew(subscription.CurrentPeriodEnd);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
    }

    [Fact]
    public void Cancelling_immediately_ends_the_subscription_now()
    {
        var subscription = TestData.SubscriptionOn(TestData.FlatPlan());

        subscription.Cancel(Jan1.AddDays(10), immediately: true);

        Assert.Equal(SubscriptionStatus.Canceled, subscription.Status);
        Assert.False(subscription.IsEntitled);
    }

    [Fact]
    public void A_canceled_subscription_cannot_be_reactivated_or_cancelled_again()
    {
        var subscription = TestData.SubscriptionOn(TestData.FlatPlan());
        subscription.Cancel(Jan1.AddDays(1), immediately: true);

        Assert.Throws<DomainException>(() => subscription.Activate(Jan1.AddDays(2)));
        Assert.Throws<DomainException>(() => subscription.Cancel(Jan1.AddDays(2)));
        Assert.Throws<DomainException>(() => subscription.Renew(subscription.CurrentPeriodEnd));
    }

    [Fact]
    public void A_paused_subscription_does_not_renew_until_it_is_resumed()
    {
        var subscription = TestData.SubscriptionOn(TestData.FlatPlan());
        subscription.Pause(Jan1.AddDays(5));

        Assert.Throws<DomainException>(() => subscription.Renew(subscription.CurrentPeriodEnd));

        subscription.Resume(subscription.CurrentPeriodEnd);
        subscription.Renew(subscription.CurrentPeriodEnd);

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Canceled, SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Canceled, SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Expired, SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Paused, SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Trialing, SubscriptionStatus.PastDue)]
    public void The_transition_table_forbids_these_moves(SubscriptionStatus from, SubscriptionStatus to)
    {
        Assert.False(Subscription.CanTransition(from, to));
    }

    [Theory]
    [InlineData(SubscriptionStatus.Trialing, SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Active, SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.PastDue, SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Paused, SubscriptionStatus.Active)]
    public void The_transition_table_allows_these_moves(SubscriptionStatus from, SubscriptionStatus to)
    {
        Assert.True(Subscription.CanTransition(from, to));
    }

    [Fact]
    public void No_transition_leads_out_of_a_terminal_state()
    {
        foreach (var terminal in new[] { SubscriptionStatus.Canceled, SubscriptionStatus.Expired })
        {
            foreach (var target in Enum.GetValues<SubscriptionStatus>())
            {
                Assert.False(Subscription.CanTransition(terminal, target));
            }
        }
    }

    [Fact]
    public void Per_seat_pricing_multiplies_by_the_seat_count()
    {
        var plan = TestData.SeatPlan(perSeat: 15m);
        var subscription = TestData.SubscriptionOn(plan, quantity: 12);

        Assert.Equal(180m, subscription.PeriodPrice(plan).Amount);
    }

    [Fact]
    public void A_yearly_subscription_reports_a_twelfth_of_its_price_as_monthly_revenue()
    {
        var plan = TestData.FlatPlan(amount: 1200m, interval: BillingInterval.Yearly);
        var subscription = TestData.SubscriptionOn(plan);

        Assert.Equal(100m, subscription.MonthlyRecurringRevenue(plan).Amount);
    }

    [Fact]
    public void A_trialing_subscription_contributes_no_recurring_revenue()
    {
        var plan = TestData.FlatPlan(amount: 99m, trialDays: 14);
        var subscription = TestData.SubscriptionOn(plan, withTrial: true);

        Assert.True(subscription.MonthlyRecurringRevenue(plan).IsZero);
    }

    [Fact]
    public void Operations_against_the_wrong_plan_are_refused()
    {
        var plan = TestData.FlatPlan();
        var other = TestData.FlatPlan(code: "OTHER");
        var subscription = TestData.SubscriptionOn(plan);

        Assert.Throws<DomainException>(() => subscription.PeriodPrice(other));
    }

    [Fact]
    public void Creating_a_subscription_raises_a_domain_event()
    {
        var subscription = TestData.SubscriptionOn(TestData.FlatPlan());

        var created = Assert.Single(subscription.DomainEvents.OfType<SubscriptionCreated>());
        Assert.Equal(subscription.Id, created.SubscriptionId);
        Assert.Equal("subscription.created", created.EventType);
    }

    [Fact]
    public void Status_changes_record_where_they_came_from_and_why()
    {
        var subscription = TestData.SubscriptionOn(TestData.FlatPlan());
        subscription.ClearDomainEvents();

        subscription.MarkPastDue(Jan1.AddDays(1), "insufficient_funds");

        var changed = Assert.Single(subscription.DomainEvents.OfType<SubscriptionStatusChanged>());
        Assert.Equal(SubscriptionStatus.Active, changed.From);
        Assert.Equal(SubscriptionStatus.PastDue, changed.To);
        Assert.Equal("insufficient_funds", changed.Reason);
    }
}
