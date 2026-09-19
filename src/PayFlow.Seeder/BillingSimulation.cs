using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Billing;
using PayFlow.Domain.Common;
using PayFlow.Domain.Customers;
using PayFlow.Domain.Plans;
using PayFlow.Domain.Subscriptions;
using PayFlow.Domain.Usage;
using PayFlow.Infrastructure.Persistence;

namespace PayFlow.Seeder;

/// <param name="TenantId">Tenant to populate.</param>
/// <param name="Name">Tenant display name, used in log output.</param>
/// <param name="Months">Months of history to generate.</param>
/// <param name="TargetCustomers">Customers signed up over the whole window.</param>
/// <param name="Seed">Random seed. The same seed reproduces the same history exactly.</param>
public sealed record SimulationSettings(
    Guid TenantId,
    string Name,
    int Months = 12,
    int TargetCustomers = 60,
    int Seed = 42);

/// <summary>
/// Generates a year of billing history by running the production billing engine against
/// a clock that advances a day at a time.
/// <para>
/// Nothing here writes an invoice, a payment or a status change directly. Signups,
/// cancellations, plan changes and metered usage are the only inputs; every invoice,
/// dunning retry, recovery and write-off in the resulting database is the billing cycle's
/// own output. That is what makes the analytics notebook worth reading - the curves are
/// the system's behaviour, not a shape someone drew.
/// </para>
/// </summary>
public sealed class BillingSimulation(
    IServiceProvider services,
    SimulationClock clock,
    ILogger<BillingSimulation> logger)
{
    /// <summary>
    /// Daily probability that an active subscription cancels. About 3.5% a month, which
    /// is a realistic mid-market SaaS churn rate and produces a visible curve over a year.
    /// </summary>
    private const double DailyChurnProbability = 0.0012;

    /// <summary>Daily probability that a subscription changes plan or seat count.</summary>
    private const double DailyPlanChangeProbability = 0.0025;

    public async Task RunAsync(SimulationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var tenantId = new TenantId(settings.TenantId);
        var random = new Random(settings.Seed);

        var start = clock.UtcNow;
        var end = start.AddMonths(settings.Months);
        var totalDays = (int)(end - start).TotalDays;

        var plans = await CreatePlansAsync(tenantId, cancellationToken);
        logger.LogInformation("{Tenant}: created {Count} plans", settings.Name, plans.Count);

        // Signups are spread with a mild upward trend, so MRR grows rather than being a
        // flat line with noise - the shape a real early-stage product has.
        var signupSchedule = BuildSignupSchedule(random, totalDays, settings.TargetCustomers);

        var customerCount = 0;
        var subscriptionIds = new List<Guid>();

        for (var day = 0; day < totalDays; day++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            clock.AdvanceTo(start.AddDays(day));

            for (var i = 0; i < signupSchedule[day]; i++)
            {
                var id = await SignUpAsync(tenantId, plans, random, ++customerCount, settings, cancellationToken);
                if (id is { } subscriptionId)
                {
                    subscriptionIds.Add(subscriptionId);
                }
            }

            await RecordUsageAsync(tenantId, random, cancellationToken);
            await ApplyChurnAndChangesAsync(tenantId, plans, random, cancellationToken);

            // The engine under test: renewals, invoicing, collection, then dunning.
            await RunBillingAsync(tenantId, cancellationToken);

            if (day % 30 == 0 && day > 0)
            {
                logger.LogInformation(
                    "{Tenant}: simulated {Day}/{Total} days, {Customers} customers signed up",
                    settings.Name,
                    day,
                    totalDays,
                    customerCount);
            }
        }

        clock.AdvanceTo(end);
        await RunBillingAsync(tenantId, cancellationToken);

        await ReportAsync(tenantId, settings, cancellationToken);
    }

    private async Task<IReadOnlyList<Plan>> CreatePlansAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        using var scope = CreateScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<PayFlowDbContext>();

        var existing = await db.Plans.Include(x => x.MeteredRates).ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            return existing;
        }

        var starter = new Plan(tenantId, "STARTER", "Starter", new Money(29m), BillingInterval.Monthly, PricingModel.FlatRate, trialDays: 14);
        var growth = new Plan(tenantId, "GROWTH", "Growth", new Money(99m), BillingInterval.Monthly, PricingModel.FlatRate, trialDays: 14);
        var team = new Plan(tenantId, "TEAM", "Team", new Money(15m), BillingInterval.Monthly, PricingModel.PerSeat);
        var scale = new Plan(tenantId, "SCALE", "Scale", new Money(990m), BillingInterval.Yearly, PricingModel.FlatRate);

        var metered = new Plan(tenantId, "METERED", "Pay as you go", new Money(19m), BillingInterval.Monthly, PricingModel.Metered);
        metered.AddMeteredRate("api_calls", new Money(0.002m), includedQuantity: 10_000m);
        metered.AddMeteredRate("storage_gb", new Money(0.15m), includedQuantity: 50m);

        var plans = new List<Plan> { starter, growth, team, scale, metered };
        db.Plans.AddRange(plans);
        await db.SaveChangesAsync(cancellationToken);

        return plans;
    }

    private async Task<Guid?> SignUpAsync(
        TenantId tenantId,
        IReadOnlyList<Plan> plans,
        Random random,
        int customerNumber,
        SimulationSettings settings,
        CancellationToken cancellationToken)
    {
        using var scope = CreateScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<PayFlowDbContext>();

        var slug = settings.Name.ToLowerInvariant().Replace(' ', '-');
        var customer = new Customer(
            tenantId,
            $"customer{customerNumber:D4}@{slug}.example",
            $"{FirstNames[random.Next(FirstNames.Length)]} {LastNames[random.Next(LastNames.Length)]}",
            "USD",
            $"crm-{customerNumber:D5}");

        customer.AttachPaymentMethod(PaymentToken(random, customerNumber));
        db.Customers.Add(customer);

        // Plan mix weighted toward the cheaper tiers, as real self-serve funnels are.
        var plan = WeightedPlan(plans, random);
        var quantity = plan.PricingModel == PricingModel.PerSeat ? random.Next(3, 40) : 1;
        var withTrial = plan.TrialDays > 0 && random.NextDouble() < 0.45;

        var subscription = new Subscription(tenantId, customer.Id, plan, clock.UtcNow, quantity, withTrial);
        db.Subscriptions.Add(subscription);

        await db.SaveChangesAsync(cancellationToken);
        return subscription.Id;
    }

    /// <summary>
    /// Assigns a payment method. Most are ordinary tokens whose outcome the simulated
    /// gateway decides; a minority are forced-failure tokens, which is what populates the
    /// dunning funnel and the decline-reason breakdown with something to look at.
    /// </summary>
    private static string PaymentToken(Random random, int customerNumber)
    {
        var roll = random.NextDouble();

        return roll switch
        {
            < 0.05 => $"pm_decline_{customerNumber:D5}",
            < 0.08 => $"pm_expired_{customerNumber:D5}",
            _ => $"pm_card_{customerNumber:D5}",
        };
    }

    private static Plan WeightedPlan(IReadOnlyList<Plan> plans, Random random)
    {
        var roll = random.NextDouble();
        var code = roll switch
        {
            < 0.38 => "STARTER",
            < 0.63 => "GROWTH",
            < 0.80 => "TEAM",
            < 0.92 => "METERED",
            _ => "SCALE",
        };

        return plans.First(x => x.Code == code);
    }

    private async Task RecordUsageAsync(TenantId tenantId, Random random, CancellationToken cancellationToken)
    {
        using var scope = CreateScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<PayFlowDbContext>();

        var metered = await db.Subscriptions
            .Join(db.Plans, s => s.PlanId, p => p.Id, (s, p) => new { Subscription = s, Plan = p })
            .Where(x => x.Plan.PricingModel == PricingModel.Metered)
            .Where(x => x.Subscription.Status == SubscriptionStatus.Active
                        || x.Subscription.Status == SubscriptionStatus.Trialing)
            .Select(x => new { x.Subscription.Id, x.Subscription.CustomerId })
            .ToListAsync(cancellationToken);

        if (metered.Count == 0)
        {
            return;
        }

        var now = clock.UtcNow;

        foreach (var subscription in metered)
        {
            // Lognormal-ish spread: most accounts sit near the included allowance and a
            // few are far above it, which is what a usage distribution really looks like.
            var calls = (decimal)Math.Round(Math.Exp(5.2 + (random.NextDouble() * 2.2)) * 3);
            db.UsageRecords.Add(new UsageRecord(
                tenantId, subscription.Id, subscription.CustomerId, "api_calls", calls, now));

            if (random.NextDouble() < 0.3)
            {
                var storage = (decimal)Math.Round(random.NextDouble() * 12, 2) + 0.01m;
                db.UsageRecords.Add(new UsageRecord(
                    tenantId, subscription.Id, subscription.CustomerId, "storage_gb", storage, now));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyChurnAndChangesAsync(
        TenantId tenantId,
        IReadOnlyList<Plan> plans,
        Random random,
        CancellationToken cancellationToken)
    {
        using var scope = CreateScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<PayFlowDbContext>();

        var live = await db.Subscriptions
            .Where(x => x.Status == SubscriptionStatus.Active)
            .ToListAsync(cancellationToken);

        if (live.Count == 0)
        {
            return;
        }

        var now = clock.UtcNow;
        var changed = false;

        foreach (var subscription in live)
        {
            if (random.NextDouble() < DailyChurnProbability)
            {
                // Voluntary churn: the customer keeps what they paid for, so this is a
                // pending cancellation that the billing cycle finalises at period end.
                subscription.Cancel(now);
                changed = true;
                continue;
            }

            if (random.NextDouble() >= DailyPlanChangeProbability)
            {
                continue;
            }

            var currentPlan = plans.FirstOrDefault(x => x.Id == subscription.PlanId);
            if (currentPlan is null)
            {
                continue;
            }

            if (currentPlan.PricingModel == PricingModel.PerSeat)
            {
                var delta = random.Next(-3, 8);
                var quantity = Math.Max(1, subscription.Quantity + delta);
                if (quantity != subscription.Quantity)
                {
                    StoreAdjustments(db, subscription, subscription.ChangeQuantity(currentPlan, quantity, now), now);
                    changed = true;
                }

                continue;
            }

            // Upgrades outnumber downgrades, which is what makes expansion revenue show
            // up as a distinct contribution in the MRR mix.
            var target = random.NextDouble() < 0.7
                ? plans.FirstOrDefault(x => x.Code == "GROWTH" && x.Id != subscription.PlanId)
                : plans.FirstOrDefault(x => x.Code == "STARTER" && x.Id != subscription.PlanId);

            if (target is not null && target.Interval == currentPlan.Interval)
            {
                StoreAdjustments(db, subscription, subscription.ChangePlan(currentPlan, target, now), now);
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static void StoreAdjustments(
        PayFlowDbContext db,
        Subscription subscription,
        Domain.Billing.ProrationResult proration,
        DateTimeOffset at)
    {
        if (proration.Credit.IsPositive)
        {
            db.SubscriptionAdjustments.Add(new Domain.Billing.SubscriptionAdjustment(
                subscription.TenantId, subscription.Id, subscription.CustomerId,
                "Unused time on previous terms", proration.Credit.Negate(), at));
        }

        if (proration.Charge.IsPositive)
        {
            db.SubscriptionAdjustments.Add(new Domain.Billing.SubscriptionAdjustment(
                subscription.TenantId, subscription.Id, subscription.CustomerId,
                "New terms for the rest of the period", proration.Charge, at));
        }
    }

    private async Task RunBillingAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        using var scope = CreateScope(tenantId);
        var cycle = scope.ServiceProvider.GetRequiredService<IBillingCycleService>();

        await cycle.RunAsync(cancellationToken);
        await cycle.RunDunningAsync(cancellationToken);
    }

    private async Task ReportAsync(TenantId tenantId, SimulationSettings settings, CancellationToken cancellationToken)
    {
        using var scope = CreateScope(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<PayFlowDbContext>();

        var subscriptions = await db.Subscriptions.CountAsync(cancellationToken);
        var active = await db.Subscriptions.CountAsync(x => x.Status == SubscriptionStatus.Active, cancellationToken);
        var invoices = await db.Invoices.CountAsync(cancellationToken);
        var payments = await db.Payments.CountAsync(cancellationToken);
        var failed = await db.Payments.CountAsync(x => x.Status == Domain.Payments.PaymentStatus.Failed, cancellationToken);

        logger.LogInformation(
            "{Tenant}: {Subscriptions} subscriptions ({Active} active), {Invoices} invoices, " +
            "{Payments} payment attempts ({Failed} declined)",
            settings.Name,
            subscriptions,
            active,
            invoices,
            payments,
            failed);
    }

    /// <summary>
    /// Builds a per-day signup count with a gentle upward trend and day-to-day noise.
    /// A flat signup rate makes every derived chart a straight line.
    /// </summary>
    private static int[] BuildSignupSchedule(Random random, int totalDays, int target)
    {
        var weights = new double[totalDays];
        var totalWeight = 0d;

        for (var day = 0; day < totalDays; day++)
        {
            var trend = 0.5 + (1.5 * day / totalDays);
            var noise = 0.6 + (random.NextDouble() * 0.8);
            weights[day] = trend * noise;
            totalWeight += weights[day];
        }

        var schedule = new int[totalDays];
        var assigned = 0;

        for (var day = 0; day < totalDays; day++)
        {
            var exact = weights[day] / totalWeight * target;
            var whole = (int)Math.Floor(exact);

            if (random.NextDouble() < exact - whole)
            {
                whole++;
            }

            schedule[day] = whole;
            assigned += whole;
        }

        // Make the totals come out at the requested number rather than near it.
        while (assigned < target)
        {
            schedule[random.Next(totalDays)]++;
            assigned++;
        }

        return schedule;
    }

    private IServiceScope CreateScope(TenantId tenantId)
    {
        var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().SetTenant(tenantId);
        return scope;
    }

    private static readonly string[] FirstNames =
    [
        "Alex", "Sam", "Jordan", "Riya", "Chen", "Priya", "Marcus", "Ingrid", "Tomas", "Yuki",
        "Fatima", "Noah", "Elena", "Kwame", "Hana", "Oscar", "Leila", "Dmitri", "Aisha", "Pablo",
    ];

    private static readonly string[] LastNames =
    [
        "Johnson", "Rivera", "Patel", "Okafor", "Nguyen", "Silva", "Kowalski", "Haddad", "Tanaka", "Muller",
        "Costa", "Bergstrom", "Ahmed", "Lindqvist", "Moreau", "Iyer", "Novak", "Fitzgerald", "Sato", "Duarte",
    ];
}
