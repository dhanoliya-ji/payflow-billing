using PayFlow.Domain.Common;
using PayFlow.Domain.Plans;
using PayFlow.Domain.Subscriptions;

namespace PayFlow.Domain.Tests;

/// <summary>
/// Builders for the objects most tests need. Keeping construction here means a change to
/// a constructor is a one-line fix rather than a sweep through every test file.
/// </summary>
internal static class TestData
{
    public static readonly DateTimeOffset Jan1 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static TenantId Tenant { get; } = TenantId.New();

    public static Plan FlatPlan(
        decimal amount = 29m,
        BillingInterval interval = BillingInterval.Monthly,
        int trialDays = 0,
        string code = "STARTER",
        string currency = "USD") =>
        new(Tenant, code, "Starter", new Money(amount, currency), interval, PricingModel.FlatRate, trialDays);

    public static Plan SeatPlan(decimal perSeat = 15m, string code = "TEAM") =>
        new(Tenant, code, "Team", new Money(perSeat), BillingInterval.Monthly, PricingModel.PerSeat);

    public static Plan MeteredPlan(
        decimal baseFee = 0m,
        decimal unitPrice = 0.002m,
        decimal included = 0m,
        string metric = "api_calls",
        string code = "METERED")
    {
        var plan = new Plan(Tenant, code, "Metered", new Money(baseFee), BillingInterval.Monthly, PricingModel.Metered);
        plan.AddMeteredRate(metric, new Money(unitPrice), included);
        return plan;
    }

    public static Subscription SubscriptionOn(
        Plan plan,
        DateTimeOffset? startsAt = null,
        int quantity = 1,
        bool withTrial = false) =>
        new(Tenant, Guid.NewGuid(), plan, startsAt ?? Jan1, quantity, withTrial);
}
