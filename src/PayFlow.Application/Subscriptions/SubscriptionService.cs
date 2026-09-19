using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Billing;
using PayFlow.Domain.Common;
using PayFlow.Domain.Customers;
using PayFlow.Domain.Plans;
using PayFlow.Domain.Subscriptions;

namespace PayFlow.Application.Subscriptions;

public sealed record SubscriptionDto(
    Guid Id,
    Guid CustomerId,
    string CustomerEmail,
    Guid PlanId,
    string PlanCode,
    SubscriptionStatus Status,
    BillingInterval Interval,
    int Quantity,
    string Currency,
    decimal MonthlyRecurringRevenue,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset? TrialEndsAt,
    bool CancelAtPeriodEnd,
    DateTimeOffset? CanceledAt,
    int ConsecutiveFailedPayments);

public sealed record CreateSubscriptionRequest(
    Guid CustomerId,
    Guid PlanId,
    int Quantity = 1,
    bool WithTrial = false,
    DateTimeOffset? StartsAt = null);

public sealed record ChangePlanRequest(Guid PlanId);

public sealed record ChangeQuantityRequest(int Quantity);

public sealed record CancelSubscriptionRequest(bool Immediately = false, string? Reason = null);

/// <param name="Subscription">The subscription after the change.</param>
/// <param name="Credit">Credit raised for the unused part of the old terms.</param>
/// <param name="Charge">Charge raised for the rest of the period on the new terms.</param>
/// <param name="Net">Net effect, carried to the next invoice.</param>
public sealed record ProrationDto(SubscriptionDto Subscription, decimal Credit, decimal Charge, decimal Net);

public interface ISubscriptionService
{
    Task<SubscriptionDto> CreateAsync(CreateSubscriptionRequest request, CancellationToken cancellationToken = default);

    Task<SubscriptionDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<SubscriptionDto>> ListAsync(
        PageRequest page,
        SubscriptionStatus? status = null,
        Guid? customerId = null,
        CancellationToken cancellationToken = default);

    Task<ProrationDto> ChangePlanAsync(Guid id, ChangePlanRequest request, CancellationToken cancellationToken = default);

    Task<ProrationDto> ChangeQuantityAsync(Guid id, ChangeQuantityRequest request, CancellationToken cancellationToken = default);

    Task<SubscriptionDto> PauseAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SubscriptionDto> ResumeAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SubscriptionDto> CancelAsync(Guid id, CancelSubscriptionRequest request, CancellationToken cancellationToken = default);
}

public sealed class SubscriptionService(IPayFlowDbContext db, ITenantContext tenant, IClock clock) : ISubscriptionService
{
    public async Task<SubscriptionDto> CreateAsync(CreateSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var customer = await db.Customers.FirstOrDefaultAsync(x => x.Id == request.CustomerId, cancellationToken)
            ?? throw new NotFoundException(nameof(Customer), request.CustomerId);

        if (customer.IsArchived)
        {
            throw new DomainException($"Customer {customer.Email} is archived and cannot take new subscriptions.");
        }

        var plan = await db.Plans.FirstOrDefaultAsync(x => x.Id == request.PlanId, cancellationToken)
            ?? throw new NotFoundException(nameof(Plan), request.PlanId);

        if (!plan.IsActive)
        {
            throw new DomainException($"Plan {plan.Code} is archived and cannot be subscribed to.");
        }

        if (!string.Equals(plan.Currency, customer.Currency, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Customer {customer.Email} bills in {customer.Currency}; plan {plan.Code} is priced in {plan.Currency}.");
        }

        var subscription = new Subscription(
            tenant.TenantId,
            customer.Id,
            plan,
            request.StartsAt ?? clock.UtcNow,
            request.Quantity,
            request.WithTrial);

        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync(cancellationToken);
        return Map(subscription, plan, customer.Email);
    }

    public async Task<SubscriptionDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var subscription = await FindAsync(id, cancellationToken);
        return await MapAsync(subscription, cancellationToken);
    }

    public async Task<PagedResult<SubscriptionDto>> ListAsync(
        PageRequest page,
        SubscriptionStatus? status = null,
        Guid? customerId = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.Subscriptions.AsNoTracking().AsQueryable();

        if (status is { } s)
        {
            query = query.Where(x => x.Status == s);
        }

        if (customerId is { } c)
        {
            query = query.Where(x => x.CustomerId == c);
        }

        var total = await query.CountAsync(cancellationToken);

        // Projected as a join rather than N+1 lookups: the dashboard lists subscriptions
        // with their plan code and customer email on every page load.
        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Skip(page.Skip)
            .Take(page.Take)
            .Join(db.Plans.AsNoTracking(), x => x.PlanId, p => p.Id, (x, p) => new { Subscription = x, Plan = p })
            .Join(
                db.Customers.AsNoTracking(),
                x => x.Subscription.CustomerId,
                c => c.Id,
                (x, c) => new { x.Subscription, x.Plan, Customer = c })
            .ToListAsync(cancellationToken);

        var items = rows.Select(x => Map(x.Subscription, x.Plan, x.Customer.Email)).ToList();
        return new PagedResult<SubscriptionDto>(items, total, page.Page, page.Take);
    }

    public async Task<ProrationDto> ChangePlanAsync(Guid id, ChangePlanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await db.InTransactionAsync(async ct =>
        {
            var subscription = await FindAsync(id, ct);
            var currentPlan = await RequirePlanAsync(subscription.PlanId, ct);
            var newPlan = await RequirePlanAsync(request.PlanId, ct);

            if (!newPlan.IsActive)
            {
                throw new DomainException($"Plan {newPlan.Code} is archived and cannot be moved to.");
            }

            var now = clock.UtcNow;
            var proration = subscription.ChangePlan(currentPlan, newPlan, now);

            StoreAdjustments(
                subscription,
                proration,
                $"Unused {currentPlan.Name} time",
                $"{newPlan.Name} for the rest of the period",
                now);

            await db.SaveChangesAsync(ct);
            var dto = await MapAsync(subscription, ct);
            return new ProrationDto(dto, proration.Credit.Amount, proration.Charge.Amount, proration.NetAmount.Amount);
        }, cancellationToken);
    }

    public async Task<ProrationDto> ChangeQuantityAsync(Guid id, ChangeQuantityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await db.InTransactionAsync(async ct =>
        {
            var subscription = await FindAsync(id, ct);
            var plan = await RequirePlanAsync(subscription.PlanId, ct);

            var now = clock.UtcNow;
            var previousQuantity = subscription.Quantity;
            var proration = subscription.ChangeQuantity(plan, request.Quantity, now);

            StoreAdjustments(
                subscription,
                proration,
                $"Unused time at {previousQuantity} seats",
                $"{request.Quantity} seats for the rest of the period",
                now);

            await db.SaveChangesAsync(ct);
            var dto = await MapAsync(subscription, ct);
            return new ProrationDto(dto, proration.Credit.Amount, proration.Charge.Amount, proration.NetAmount.Amount);
        }, cancellationToken);
    }

    public async Task<SubscriptionDto> PauseAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var subscription = await FindAsync(id, cancellationToken);
        subscription.Pause(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(subscription, cancellationToken);
    }

    public async Task<SubscriptionDto> ResumeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var subscription = await FindAsync(id, cancellationToken);
        var now = clock.UtcNow;

        // "Resume" covers two distinct intents: undo a pending cancellation, or restart a
        // paused subscription. Which one applies is unambiguous from the current state.
        if (subscription.CancelAtPeriodEnd)
        {
            subscription.ResumeCancellation(now);
        }
        else
        {
            subscription.Resume(now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(subscription, cancellationToken);
    }

    public async Task<SubscriptionDto> CancelAsync(Guid id, CancelSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var subscription = await FindAsync(id, cancellationToken);
        subscription.Cancel(clock.UtcNow, request.Immediately, request.Reason ?? "canceled by request");
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(subscription, cancellationToken);
    }

    internal static SubscriptionDto Map(Subscription subscription, Plan plan, string customerEmail) => new(
        subscription.Id,
        subscription.CustomerId,
        customerEmail,
        subscription.PlanId,
        plan.Code,
        subscription.Status,
        subscription.Interval,
        subscription.Quantity,
        subscription.Currency,
        subscription.MonthlyRecurringRevenue(plan).Amount,
        subscription.CurrentPeriodStart,
        subscription.CurrentPeriodEnd,
        subscription.TrialEndsAt,
        subscription.CancelAtPeriodEnd,
        subscription.CanceledAt,
        subscription.ConsecutiveFailedPayments);

    private void StoreAdjustments(
        Subscription subscription,
        ProrationResult proration,
        string creditDescription,
        string chargeDescription,
        DateTimeOffset at)
    {
        if (proration.Credit.IsPositive)
        {
            db.SubscriptionAdjustments.Add(new SubscriptionAdjustment(
                subscription.TenantId,
                subscription.Id,
                subscription.CustomerId,
                creditDescription,
                proration.Credit.Negate(),
                at));
        }

        if (proration.Charge.IsPositive)
        {
            db.SubscriptionAdjustments.Add(new SubscriptionAdjustment(
                subscription.TenantId,
                subscription.Id,
                subscription.CustomerId,
                chargeDescription,
                proration.Charge,
                at));
        }
    }

    private async Task<SubscriptionDto> MapAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        var plan = await RequirePlanAsync(subscription.PlanId, cancellationToken);
        var email = await db.Customers
            .AsNoTracking()
            .Where(x => x.Id == subscription.CustomerId)
            .Select(x => x.Email)
            .FirstOrDefaultAsync(cancellationToken) ?? "";

        return Map(subscription, plan, email);
    }

    private async Task<Subscription> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Subscriptions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw new NotFoundException(nameof(Subscription), id);

    private async Task<Plan> RequirePlanAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Plans.Include(x => x.MeteredRates).FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw new NotFoundException(nameof(Plan), id);
}
