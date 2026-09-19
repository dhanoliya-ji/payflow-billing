using PayFlow.Domain;
using Xunit;

namespace PayFlow.Domain.Tests;

public sealed class SubscriptionTests
{
    [Fact]
    public void Active_subscription_can_become_past_due_and_recover()
    {
        var subscription = new Subscription(TenantId.New(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        subscription.MarkPastDue();
        subscription.Activate();
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
    }

    [Fact]
    public void Canceled_subscription_cannot_be_reactivated()
    {
        var subscription = new Subscription(TenantId.New(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        subscription.Cancel(DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(subscription.Activate);
    }

    [Fact]
    public void Yearly_subscription_uses_yearly_billing_period()
    {
        var startsAt = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var subscription = new Subscription(TenantId.New(), Guid.NewGuid(), Guid.NewGuid(), startsAt, BillingInterval.Yearly, trial: false);

        Assert.Equal(BillingInterval.Yearly, subscription.BillingInterval);
        Assert.Equal(startsAt.AddYears(1), subscription.CurrentPeriodEnd);
    }

    [Fact]
    public void Invoice_can_be_voided_before_payment_and_payment_can_be_refunded()
    {
        var tenant = TenantId.New();
        var invoice = new Subscription.Invoice(tenant, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddDays(5));
        invoice.AddLine("Starter plan", 1, 29m);

        invoice.Void();
        Assert.Equal(Subscription.InvoiceStatus.Void, invoice.Status);

        var payment = new Subscription.Payment(tenant, invoice.Id, invoice.Total, "ref-123");
        payment.Refund();
        Assert.Equal(Subscription.PaymentStatus.Refunded, payment.Status);
    }
}
