using PayFlow.Domain.Common;
using PayFlow.Domain.Payments;
using Xunit;

namespace PayFlow.Domain.Tests;

public sealed class DunningPolicyTests
{
    private static readonly DateTimeOffset Jan1 = TestData.Jan1;

    [Fact]
    public void The_default_policy_allows_the_first_charge_plus_every_retry()
    {
        Assert.Equal(5, DunningPolicy.Default.MaxAttempts);
        Assert.Equal(4, DunningPolicy.Default.RetryOffsets.Count);
    }

    [Fact]
    public void Retries_widen_rather_than_repeating_immediately()
    {
        var offsets = DunningPolicy.Default.RetryOffsets;

        for (var i = 1; i < offsets.Count; i++)
        {
            Assert.True(offsets[i] > offsets[i - 1], $"Offset {i} should be later than offset {i - 1}.");
        }
    }

    [Fact]
    public void Each_failure_schedules_the_next_offset_in_order()
    {
        var policy = DunningPolicy.Default;

        Assert.Equal(Jan1.AddDays(1), policy.NextAttemptAfter(1, Jan1));
        Assert.Equal(Jan1.AddDays(3), policy.NextAttemptAfter(2, Jan1));
        Assert.Equal(Jan1.AddDays(5), policy.NextAttemptAfter(3, Jan1));
        Assert.Equal(Jan1.AddDays(7), policy.NextAttemptAfter(4, Jan1));
    }

    [Fact]
    public void Once_the_schedule_runs_out_there_is_no_next_attempt()
    {
        Assert.Null(DunningPolicy.Default.NextAttemptAfter(5, Jan1));
        Assert.True(DunningPolicy.Default.IsExhausted(5));
    }

    [Fact]
    public void Asking_for_a_retry_before_any_attempt_was_made_is_a_programming_error()
    {
        Assert.Throws<DomainValidationException>(() => DunningPolicy.Default.NextAttemptAfter(0, Jan1));
    }

    [Fact]
    public void A_policy_with_no_retries_is_rejected()
    {
        var policy = new DunningPolicy { RetryOffsets = [], PaymentTerms = TimeSpan.Zero };

        Assert.Throws<DomainValidationException>(policy.Validate);
    }

    [Fact]
    public void Non_positive_retry_offsets_are_rejected()
    {
        var policy = new DunningPolicy { RetryOffsets = [TimeSpan.Zero], PaymentTerms = TimeSpan.Zero };

        Assert.Throws<DomainValidationException>(policy.Validate);
    }

    [Fact]
    public void The_net_thirty_policy_leaves_the_subscription_alone_when_retries_run_out()
    {
        // Enterprise invoices are chased by a human, not expired automatically.
        Assert.False(DunningPolicy.NetThirty.ExpireSubscriptionOnExhaustion);
        Assert.Equal(TimeSpan.FromDays(30), DunningPolicy.NetThirty.PaymentTerms);
    }
}

public sealed class PaymentTests
{
    private static readonly DateTimeOffset Jan1 = TestData.Jan1;

    private static Payment Captured(decimal amount = 100m) =>
        Payment.Succeeded(TestData.Tenant, Guid.NewGuid(), Guid.NewGuid(), new Money(amount), "gw_123", Jan1);

    [Fact]
    public void A_successful_payment_records_its_gateway_reference()
    {
        var payment = Captured();

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal("gw_123", payment.Reference);
        Assert.Null(payment.FailureCode);
    }

    [Fact]
    public void A_failed_attempt_is_stored_with_its_reason_and_attempt_number()
    {
        var payment = Payment.Failed(
            TestData.Tenant, Guid.NewGuid(), Guid.NewGuid(), new Money(100m),
            "insufficient_funds", "The card has insufficient funds.", Jan1, attemptNumber: 3);

        // Failures are rows, not discarded: the dunning funnel and the decline breakdown
        // are both built from them.
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(3, payment.AttemptNumber);
        Assert.True(payment.NetAmount.IsZero);
    }

    [Fact]
    public void A_partial_refund_reduces_the_net_amount()
    {
        var payment = Captured(100m);

        payment.Refund(new Money(30m), Jan1.AddDays(5));

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(30m, payment.RefundedAmount.Amount);
        Assert.Equal(70m, payment.NetAmount.Amount);
    }

    [Fact]
    public void Refunding_more_than_was_captured_is_refused()
    {
        var payment = Captured(100m);
        payment.Refund(new Money(60m), Jan1.AddDays(1));

        Assert.Throws<DomainException>(() => payment.Refund(new Money(50m), Jan1.AddDays(2)));
    }

    [Fact]
    public void A_failed_payment_cannot_be_refunded_because_it_captured_nothing()
    {
        var payment = Payment.Failed(
            TestData.Tenant, Guid.NewGuid(), Guid.NewGuid(), new Money(100m),
            "card_expired", "The card has expired.", Jan1);

        Assert.Throws<DomainException>(() => payment.Refund(new Money(10m), Jan1));
    }

    [Fact]
    public void Refunding_in_another_currency_is_refused()
    {
        var payment = Captured(100m);

        Assert.Throws<DomainException>(() => payment.Refund(new Money(10m, "EUR"), Jan1));
    }

    [Fact]
    public void A_zero_or_negative_payment_is_rejected()
    {
        Assert.Throws<DomainValidationException>(() =>
            Payment.Succeeded(TestData.Tenant, Guid.NewGuid(), Guid.NewGuid(), Money.Zero(), "ref", Jan1));
    }
}
