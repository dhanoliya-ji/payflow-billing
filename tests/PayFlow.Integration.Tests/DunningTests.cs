using Microsoft.Extensions.DependencyInjection;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Billing;
using PayFlow.Application.Customers;
using PayFlow.Application.Invoicing;
using PayFlow.Application.Payments;
using PayFlow.Application.Plans;
using PayFlow.Application.Subscriptions;
using PayFlow.Domain.Invoicing;
using PayFlow.Domain.Payments;
using PayFlow.Domain.Subscriptions;
using Xunit;

namespace PayFlow.Integration.Tests;

/// <summary>
/// The collection and retry path, driven with a scripted gateway so each branch is
/// reached deliberately rather than by chance.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DunningTests(PostgresFixture postgres)
{
    private static readonly DunningPolicy ThreeRetries = new()
    {
        PaymentTerms = TimeSpan.Zero,
        RetryOffsets = [TimeSpan.FromDays(1), TimeSpan.FromDays(3), TimeSpan.FromDays(5)],
    };

    private static async Task<SubscriptionDto> ArrangeAsync(BillingHarness harness, decimal amount = 50m) =>
        await harness.AsTenantAsync(async services =>
        {
            var customer = await services.GetRequiredService<ICustomerService>().CreateAsync(
                new CreateCustomerRequest("dunning@example.com", "Dunning Test", PaymentMethodToken: "pm_test"));

            var plan = await services.GetRequiredService<IPlanService>().CreateAsync(
                new CreatePlanRequest("STARTER", "Starter", amount, "Monthly"));

            return await services.GetRequiredService<ISubscriptionService>().CreateAsync(
                new CreateSubscriptionRequest(customer.Id, plan.Id));
        });

    [Fact]
    public async Task A_declined_charge_puts_the_subscription_past_due_and_schedules_a_retry()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres, ThreeRetries);
        var subscription = await ArrangeAsync(harness);

        harness.Gateway.Enqueue(ChargeResult.Declined("insufficient_funds", "No funds."));
        harness.Clock.UtcNow = subscription.PeriodEnd.AddMinutes(1);

        var report = await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunAsync());

        Assert.Equal(1, report.PaymentsFailed);
        Assert.Equal(0, report.PaymentsCollected);

        await harness.AsTenantAsync(async services =>
        {
            var invoices = await services.GetRequiredService<IInvoiceService>().ListAsync(PageRequest.Of(1, 10));
            var invoice = Assert.Single(invoices.Items);

            Assert.Equal(InvoiceStatus.PastDue, invoice.Status);
            Assert.Equal(1, invoice.AttemptCount);
            Assert.Equal(harness.Clock.UtcNow.AddDays(1), invoice.NextAttemptAt);

            var updated = await services.GetRequiredService<ISubscriptionService>().GetAsync(subscription.Id);
            Assert.Equal(SubscriptionStatus.PastDue, updated.Status);
        });
    }

    [Fact]
    public async Task A_retry_that_succeeds_settles_the_invoice_and_restores_the_subscription()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres, ThreeRetries);
        var subscription = await ArrangeAsync(harness);

        harness.Gateway.Enqueue(
            ChargeResult.Declined("insufficient_funds", "No funds."),
            ChargeResult.Success("ref_recovered"));

        harness.Clock.UtcNow = subscription.PeriodEnd.AddMinutes(1);
        await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunAsync());

        // Nothing is due until the first retry offset has elapsed.
        var tooEarly = await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunDunningAsync());
        Assert.Equal(0, tooEarly.PaymentsCollected);

        harness.Clock.AdvanceDays(1.1);
        var recovered = await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunDunningAsync());

        Assert.Equal(1, recovered.PaymentsCollected);

        await harness.AsTenantAsync(async services =>
        {
            var invoices = await services.GetRequiredService<IInvoiceService>().ListAsync(PageRequest.Of(1, 10));
            Assert.Equal(InvoiceStatus.Paid, invoices.Items[0].Status);
            Assert.Null(invoices.Items[0].NextAttemptAt);

            var updated = await services.GetRequiredService<ISubscriptionService>().GetAsync(subscription.Id);
            Assert.Equal(SubscriptionStatus.Active, updated.Status);
            Assert.Equal(0, updated.ConsecutiveFailedPayments);
        });
    }

    [Fact]
    public async Task Exhausting_the_schedule_writes_the_invoice_off_and_expires_the_subscription()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres, ThreeRetries);
        var subscription = await ArrangeAsync(harness);

        harness.Gateway.Default = ChargeResult.Declined("do_not_honor", "Declined.");

        harness.Clock.UtcNow = subscription.PeriodEnd.AddMinutes(1);
        await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunAsync());

        // Three retries at +1, +3 and +5 days from each failure.
        foreach (var _ in Enumerable.Range(0, 3))
        {
            harness.Clock.AdvanceDays(6);
            await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunDunningAsync());
        }

        await harness.AsTenantAsync(async services =>
        {
            var invoices = await services.GetRequiredService<IInvoiceService>().ListAsync(PageRequest.Of(1, 10));
            var invoice = invoices.Items[0];

            Assert.Equal(InvoiceStatus.Uncollectible, invoice.Status);
            Assert.Equal(ThreeRetries.MaxAttempts, invoice.AttemptCount);
            Assert.Null(invoice.NextAttemptAt);

            var updated = await services.GetRequiredService<ISubscriptionService>().GetAsync(subscription.Id);
            Assert.Equal(SubscriptionStatus.Expired, updated.Status);

            // Every attempt is kept, successes and failures alike, because the dunning
            // funnel and the decline breakdown are built from them.
            var payments = await services.GetRequiredService<IPaymentService>().ListAsync(PageRequest.Of(1, 20));
            Assert.Equal(ThreeRetries.MaxAttempts, payments.Items.Count);
            Assert.All(payments.Items, x => Assert.Equal(PaymentStatus.Failed, x.Status));
            Assert.Equal(
                Enumerable.Range(1, ThreeRetries.MaxAttempts),
                payments.Items.Select(x => x.AttemptNumber).OrderBy(x => x));
        });
    }

    [Fact]
    public async Task A_permanent_decline_is_written_off_without_burning_the_retry_schedule()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres, ThreeRetries);
        var subscription = await ArrangeAsync(harness);

        // An expired card fails identically on every retry, so retrying it only delays
        // the write-off and counts against the merchant's retry allowance.
        harness.Gateway.Default = ChargeResult.Declined("card_expired", "The card has expired.", retriable: false);

        harness.Clock.UtcNow = subscription.PeriodEnd.AddMinutes(1);
        var report = await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunAsync());

        Assert.Equal(1, report.InvoicesWrittenOff);

        await harness.AsTenantAsync(async services =>
        {
            var invoices = await services.GetRequiredService<IInvoiceService>().ListAsync(PageRequest.Of(1, 10));
            var invoice = invoices.Items[0];

            Assert.Equal(InvoiceStatus.Uncollectible, invoice.Status);
            Assert.Equal(1, invoice.AttemptCount);
            Assert.Null(invoice.NextAttemptAt);
        });

        Assert.Single(harness.Gateway.Charges);
    }

    [Fact]
    public async Task A_charge_carries_a_stable_idempotency_key_per_attempt()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres, ThreeRetries);
        var subscription = await ArrangeAsync(harness);

        harness.Gateway.Enqueue(
            ChargeResult.Declined("insufficient_funds", "No funds."),
            ChargeResult.Declined("insufficient_funds", "No funds."));

        harness.Clock.UtcNow = subscription.PeriodEnd.AddMinutes(1);
        await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunAsync());

        harness.Clock.AdvanceDays(1.1);
        await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunDunningAsync());

        // Derived from the invoice and attempt number rather than random, so a worker
        // that crashes after charging but before committing re-sends the same key and
        // the gateway returns the original capture instead of charging twice.
        Assert.Equal(2, harness.Gateway.Charges.Count);
        Assert.EndsWith("attempt-1", harness.Gateway.Charges[0].IdempotencyKey, StringComparison.Ordinal);
        Assert.EndsWith("attempt-2", harness.Gateway.Charges[1].IdempotencyKey, StringComparison.Ordinal);
        Assert.NotEqual(harness.Gateway.Charges[0].IdempotencyKey, harness.Gateway.Charges[1].IdempotencyKey);
    }

    [Fact]
    public async Task A_customer_with_no_payment_method_is_left_alone_rather_than_recorded_as_a_decline()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres, ThreeRetries);

        var subscription = await harness.AsTenantAsync(async services =>
        {
            var customer = await services.GetRequiredService<ICustomerService>().CreateAsync(
                new CreateCustomerRequest("invoice-only@example.com", "Invoice Only"));

            var plan = await services.GetRequiredService<IPlanService>().CreateAsync(
                new CreatePlanRequest("ENTERPRISE", "Enterprise", 5000m, "Yearly"));

            return await services.GetRequiredService<ISubscriptionService>().CreateAsync(
                new CreateSubscriptionRequest(customer.Id, plan.Id));
        });

        harness.Clock.UtcNow = subscription.PeriodEnd.AddMinutes(1);
        var report = await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunAsync());

        Assert.Equal(1, report.InvoicesIssued);

        // Nothing to charge against is not the customer's card failing; recording a
        // decline here would corrupt the decline statistics and start dunning a
        // customer who was always going to pay by bank transfer.
        Assert.Equal(0, report.PaymentsFailed);
        Assert.Empty(harness.Gateway.Charges);

        await harness.AsTenantAsync(async services =>
        {
            var invoices = await services.GetRequiredService<IInvoiceService>().ListAsync(PageRequest.Of(1, 10));
            Assert.Equal(InvoiceStatus.Open, invoices.Items[0].Status);
        });
    }

    [Fact]
    public async Task A_refund_reopens_the_invoice_for_the_refunded_amount()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres, ThreeRetries);
        var subscription = await ArrangeAsync(harness, amount: 80m);

        harness.Clock.UtcNow = subscription.PeriodEnd.AddMinutes(1);
        await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunAsync());

        await harness.AsTenantAsync(async services =>
        {
            var payments = services.GetRequiredService<IPaymentService>();
            var all = await payments.ListAsync(PageRequest.Of(1, 10));
            var payment = all.Items[0];

            var refunded = await payments.RefundAsync(payment.Id, new RefundPaymentRequest(30m));
            Assert.Equal(30m, refunded.RefundedAmount);
            Assert.Equal(PaymentStatus.Refunded, refunded.Status);

            var invoices = await services.GetRequiredService<IInvoiceService>().ListAsync(PageRequest.Of(1, 10));
            var invoice = invoices.Items[0];

            Assert.Equal(30m, invoice.Balance);
            Assert.True(invoice.Status.IsPayable());
        });
    }
}
