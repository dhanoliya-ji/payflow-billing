using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Billing;
using PayFlow.Application.Customers;
using PayFlow.Application.Invoicing;
using PayFlow.Application.Payments;
using PayFlow.Application.Plans;
using PayFlow.Application.Subscriptions;
using PayFlow.Application.Usage;
using PayFlow.Domain.Common;
using PayFlow.Domain.Invoicing;
using PayFlow.Domain.Payments;
using PayFlow.Domain.Subscriptions;
using PayFlow.Infrastructure.Persistence;
using Xunit;

namespace PayFlow.Integration.Tests;

/// <summary>
/// End-to-end billing runs against a real database and the real services. These cover
/// what the domain tests cannot: that the pieces are wired together, that the
/// transactions hold, and that the tenant filters apply.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BillingCycleTests(PostgresFixture postgres)
{
    private static async Task<(CustomerDto Customer, PlanDto Plan, SubscriptionDto Subscription)> ArrangeAsync(
        BillingHarness harness,
        TenantId tenant,
        CreatePlanRequest plan,
        bool withTrial = false,
        int quantity = 1,
        string paymentToken = "pm_ok_test")
    {
        return await harness.AsTenantAsync(tenant, async services =>
        {
            var customers = services.GetRequiredService<ICustomerService>();
            var plans = services.GetRequiredService<IPlanService>();
            var subscriptions = services.GetRequiredService<ISubscriptionService>();

            var customer = await customers.CreateAsync(new CreateCustomerRequest(
                $"{Guid.NewGuid():N}@example.com", "Test Customer", PaymentMethodToken: paymentToken));

            var created = await plans.CreateAsync(plan);

            var subscription = await subscriptions.CreateAsync(
                new CreateSubscriptionRequest(customer.Id, created.Id, quantity, withTrial));

            return (customer, created, subscription);
        });
    }

    [Fact]
    public async Task A_full_period_is_renewed_invoiced_and_collected()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres);

        var (_, _, subscription) = await ArrangeAsync(
            harness, harness.TenantA, new CreatePlanRequest("STARTER", "Starter", 29m, "Monthly"));

        // Nothing is due yet.
        var early = await harness.AsTenantAsync(s =>
            s.GetRequiredService<IBillingCycleService>().RunAsync());
        Assert.False(early.DidWork);

        harness.Clock.UtcNow = subscription.PeriodEnd.AddMinutes(1);

        var report = await harness.AsTenantAsync(s =>
            s.GetRequiredService<IBillingCycleService>().RunAsync());

        Assert.Equal(1, report.SubscriptionsRenewed);
        Assert.Equal(1, report.InvoicesIssued);
        Assert.Equal(1, report.PaymentsCollected);
        Assert.Equal(29m, report.AmountCollected["USD"]);

        await harness.AsTenantAsync(async services =>
        {
            var invoices = await services.GetRequiredService<IInvoiceService>()
                .ListAsync(PageRequest.Of(1, 10));

            var invoice = Assert.Single(invoices.Items);
            Assert.Equal(InvoiceStatus.Paid, invoice.Status);
            Assert.Equal(1, invoice.Number);
            Assert.Equal(29m, invoice.Total);
            Assert.Equal(0m, invoice.Balance);
        });
    }

    [Fact]
    public async Task Running_the_cycle_twice_does_not_bill_the_same_period_twice()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres);

        var (_, _, subscription) = await ArrangeAsync(
            harness, harness.TenantA, new CreatePlanRequest("STARTER", "Starter", 29m, "Monthly"));

        harness.Clock.UtcNow = subscription.PeriodEnd.AddMinutes(1);

        await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunAsync());
        var second = await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunAsync());

        // The subscription's new period has not ended, so there is nothing to do.
        Assert.Equal(0, second.InvoicesIssued);

        await harness.AsTenantAsync(async services =>
        {
            var count = await services.GetRequiredService<PayFlowDbContext>().Invoices.CountAsync();
            Assert.Equal(1, count);
        });
    }

    [Fact]
    public async Task A_trial_period_is_never_invoiced()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres);

        var (_, _, subscription) = await ArrangeAsync(
            harness,
            harness.TenantA,
            new CreatePlanRequest("STARTER", "Starter", 29m, "Monthly", TrialDays: 14),
            withTrial: true);

        Assert.Equal(SubscriptionStatus.Trialing, subscription.Status);

        harness.Clock.UtcNow = subscription.PeriodEnd.AddMinutes(1);
        var report = await harness.AsTenantAsync(s =>
            s.GetRequiredService<IBillingCycleService>().RunAsync());

        Assert.Equal(1, report.TrialsConverted);
        Assert.Equal(0, report.InvoicesIssued);

        var converted = await harness.AsTenantAsync(s =>
            s.GetRequiredService<ISubscriptionService>().GetAsync(subscription.Id));

        Assert.Equal(SubscriptionStatus.Active, converted.Status);

        // The first paid period starts where the trial ended, so no free day is billed.
        Assert.Equal(subscription.PeriodEnd, converted.PeriodStart);
    }

    [Fact]
    public async Task Metered_usage_is_invoiced_at_the_plan_rate_and_only_once()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres);

        var (_, _, subscription) = await ArrangeAsync(
            harness,
            harness.TenantA,
            new CreatePlanRequest(
                "METERED", "Pay as you go", 19m, "Monthly",
                PricingModel: "Metered",
                MeteredRates: [new CreateMeteredRateRequest("api_calls", 0.002m, 10_000m)]));

        await harness.AsTenantAsync(async services =>
        {
            var usage = services.GetRequiredService<IUsageService>();
            await usage.RecordAsync(new RecordUsageRequest(subscription.Id, "api_calls", 25_000m));
        });

        harness.Clock.UtcNow = subscription.PeriodEnd.AddMinutes(1);
        await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunAsync());

        await harness.AsTenantAsync(async services =>
        {
            var invoices = await services.GetRequiredService<IInvoiceService>().ListAsync(PageRequest.Of(1, 10));
            var invoice = Assert.Single(invoices.Items);

            // $19 base + (25,000 - 10,000 included) x $0.002 = $19 + $30.
            Assert.Equal(49m, invoice.Total);
            Assert.Contains(invoice.Lines, x => x.Kind == InvoiceLineKind.Usage && x.Amount == 30m);

            var records = await services.GetRequiredService<IUsageService>()
                .ListAsync(PageRequest.Of(1, 10), subscription.Id);
            Assert.All(records.Items, x => Assert.True(x.Invoiced));
        });
    }

    [Fact]
    public async Task Retried_usage_reports_with_the_same_key_are_recorded_once()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres);

        var (_, _, subscription) = await ArrangeAsync(
            harness,
            harness.TenantA,
            new CreatePlanRequest(
                "METERED", "Pay as you go", 0m, "Monthly",
                PricingModel: "Metered",
                MeteredRates: [new CreateMeteredRateRequest("api_calls", 1m)]));

        await harness.AsTenantAsync(async services =>
        {
            var usage = services.GetRequiredService<IUsageService>();
            var request = new RecordUsageRequest(subscription.Id, "api_calls", 100m, IdempotencyKey: "agent-batch-7");

            var first = await usage.RecordAsync(request);
            var replay = await usage.RecordAsync(request);

            // A metering agent retrying a timed-out report must not double the bill.
            Assert.Equal(first.Id, replay.Id);

            var all = await usage.ListAsync(PageRequest.Of(1, 10), subscription.Id);
            Assert.Single(all.Items);
        });
    }

    [Fact]
    public async Task A_mid_cycle_upgrade_appears_as_proration_lines_on_the_next_invoice()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres);

        var (customer, starter, subscription) = await ArrangeAsync(
            harness, harness.TenantA, new CreatePlanRequest("STARTER", "Starter", 100m, "Monthly"));

        var growth = await harness.AsTenantAsync(s =>
            s.GetRequiredService<IPlanService>().CreateAsync(new CreatePlanRequest("GROWTH", "Growth", 300m, "Monthly")));

        // Exactly halfway through the period.
        var halfway = subscription.PeriodStart
            + TimeSpan.FromTicks((subscription.PeriodEnd - subscription.PeriodStart).Ticks / 2);
        harness.Clock.UtcNow = halfway;

        var proration = await harness.AsTenantAsync(s =>
            s.GetRequiredService<ISubscriptionService>()
                .ChangePlanAsync(subscription.Id, new ChangePlanRequest(growth.Id)));

        Assert.Equal(50m, proration.Credit);
        Assert.Equal(150m, proration.Charge);
        Assert.Equal(100m, proration.Net);

        harness.Clock.UtcNow = subscription.PeriodEnd.AddMinutes(1);
        await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunAsync());

        await harness.AsTenantAsync(async services =>
        {
            var invoices = await services.GetRequiredService<IInvoiceService>().ListAsync(PageRequest.Of(1, 10));
            var invoice = Assert.Single(invoices.Items);

            // $300 recurring on the new plan, minus $50 credit, plus $150 catch-up.
            Assert.Equal(400m, invoice.Total);
            Assert.Contains(invoice.Lines, x => x.Kind == InvoiceLineKind.ProrationCredit && x.Amount == -50m);
            Assert.Contains(invoice.Lines, x => x.Kind == InvoiceLineKind.ProrationCharge && x.Amount == 150m);
        });

        Assert.Equal(customer.Id, subscription.CustomerId);
        Assert.Equal("STARTER", starter.Code);
    }

    [Fact]
    public async Task Invoice_numbers_are_sequential_and_gap_free_within_a_tenant()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres);

        var subscriptions = new List<SubscriptionDto>();
        for (var i = 0; i < 4; i++)
        {
            var (_, _, subscription) = await ArrangeAsync(
                harness, harness.TenantA, new CreatePlanRequest($"PLAN{i}", $"Plan {i}", 10m + i, "Monthly"));
            subscriptions.Add(subscription);
        }

        harness.Clock.UtcNow = subscriptions.Max(x => x.PeriodEnd).AddMinutes(1);
        await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunAsync());

        await harness.AsTenantAsync(async services =>
        {
            var numbers = await services.GetRequiredService<PayFlowDbContext>().Invoices
                .Where(x => x.Number != null)
                .Select(x => x.Number!.Value)
                .OrderBy(x => x)
                .ToListAsync();

            Assert.Equal([1L, 2L, 3L, 4L], numbers);
        });
    }

    [Fact]
    public async Task Each_tenant_gets_its_own_invoice_sequence_starting_at_one()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres);

        foreach (var tenant in new[] { harness.TenantA, harness.TenantB })
        {
            var (_, _, subscription) = await ArrangeAsync(
                harness, tenant, new CreatePlanRequest("STARTER", "Starter", 29m, "Monthly"));

            harness.Clock.UtcNow = subscription.PeriodEnd.AddMinutes(1);
            await harness.AsTenantAsync(tenant, s => s.GetRequiredService<IBillingCycleService>().RunAsync());
        }

        foreach (var tenant in new[] { harness.TenantA, harness.TenantB })
        {
            await harness.AsTenantAsync(tenant, async services =>
            {
                var invoices = await services.GetRequiredService<IInvoiceService>().ListAsync(PageRequest.Of(1, 10));

                // A tenant whose invoices jump because another tenant consumed the
                // numbers in between has an audit problem.
                Assert.Single(invoices.Items);
                Assert.Equal(1, invoices.Items[0].Number);
            });
        }
    }

    [Fact]
    public async Task One_tenant_cannot_see_or_reach_another_tenant_s_data()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres);

        var (customerA, planA, subscriptionA) = await ArrangeAsync(
            harness, harness.TenantA, new CreatePlanRequest("STARTER", "Starter", 29m, "Monthly"));

        await ArrangeAsync(harness, harness.TenantB, new CreatePlanRequest("OTHER", "Other", 99m, "Monthly"));

        await harness.AsTenantAsync(harness.TenantB, async services =>
        {
            var customers = await services.GetRequiredService<ICustomerService>().ListAsync(PageRequest.Of(1, 50));
            Assert.DoesNotContain(customers.Items, x => x.Id == customerA.Id);

            var plans = await services.GetRequiredService<IPlanService>().ListAsync();
            Assert.DoesNotContain(plans, x => x.Id == planA.Id);

            // Fetching by a known id from the wrong tenant must be indistinguishable from
            // the row not existing - anything else confirms another tenant holds it.
            await Assert.ThrowsAsync<NotFoundException>(() =>
                services.GetRequiredService<ISubscriptionService>().GetAsync(subscriptionA.Id));

            await Assert.ThrowsAsync<NotFoundException>(() =>
                services.GetRequiredService<ICustomerService>().GetAsync(customerA.Id));
        });
    }

    [Fact]
    public async Task Voiding_an_invoice_releases_its_usage_so_it_can_be_rebilled()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres);

        var (_, _, subscription) = await ArrangeAsync(
            harness,
            harness.TenantA,
            new CreatePlanRequest(
                "METERED", "Metered", 0m, "Monthly",
                PricingModel: "Metered",
                MeteredRates: [new CreateMeteredRateRequest("api_calls", 0.5m)]));

        await harness.AsTenantAsync(s =>
            s.GetRequiredService<IUsageService>().RecordAsync(new RecordUsageRequest(subscription.Id, "api_calls", 200m)));

        harness.Clock.UtcNow = subscription.PeriodEnd.AddMinutes(1);
        await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunAsync());

        await harness.AsTenantAsync(async services =>
        {
            var invoiceService = services.GetRequiredService<IInvoiceService>();
            var invoices = await invoiceService.ListAsync(PageRequest.Of(1, 10));
            var invoice = invoices.Items[0];

            // Paid invoices cannot be voided, so reverse the payment first.
            var payments = await services.GetRequiredService<IPaymentService>()
                .ListAsync(PageRequest.Of(1, 10), invoice.Id);
            await services.GetRequiredService<IPaymentService>()
                .RefundAsync(payments.Items[0].Id, new RefundPaymentRequest());

            var voided = await invoiceService.VoidAsync(invoice.Id, "issued in error");
            Assert.Equal(InvoiceStatus.Void, voided.Status);

            // Without the release, the billable usage would be destroyed with the invoice.
            var usage = await services.GetRequiredService<IUsageService>()
                .ListAsync(PageRequest.Of(1, 10), subscription.Id);
            Assert.All(usage.Items, x => Assert.False(x.Invoiced));
        });
    }

    [Fact]
    public async Task Domain_events_reach_the_outbox_in_the_same_transaction()
    {
        await using var harness = await BillingHarness.CreateAsync(postgres);

        var (_, _, subscription) = await ArrangeAsync(
            harness, harness.TenantA, new CreatePlanRequest("STARTER", "Starter", 29m, "Monthly"));

        harness.Clock.UtcNow = subscription.PeriodEnd.AddMinutes(1);
        await harness.AsTenantAsync(s => s.GetRequiredService<IBillingCycleService>().RunAsync());

        await harness.AsTenantAsync(async services =>
        {
            var events = await services.GetRequiredService<PayFlowDbContext>().OutboxMessages
                .Select(x => x.EventType)
                .ToListAsync();

            Assert.Contains("subscription.created", events);
            Assert.Contains("invoice.issued", events);
            Assert.Contains("invoice.paid", events);
            Assert.Contains("subscription.renewed", events);
            Assert.All(events, x => Assert.False(string.IsNullOrWhiteSpace(x)));
        });
    }
}
