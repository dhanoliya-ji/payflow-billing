using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Common;
using PayFlow.Domain.Plans;
using PayFlow.Domain.Subscriptions;
using PayFlow.Domain.Usage;

namespace PayFlow.Application.Usage;

public sealed record UsageRecordDto(
    Guid Id,
    Guid SubscriptionId,
    Guid CustomerId,
    string Metric,
    decimal Quantity,
    DateTimeOffset RecordedAt,
    bool Invoiced,
    Guid? InvoiceId);

public sealed record RecordUsageRequest(
    Guid SubscriptionId,
    string Metric,
    decimal Quantity,
    DateTimeOffset? RecordedAt = null,
    string? IdempotencyKey = null);

/// <param name="Metric">The metered dimension.</param>
/// <param name="TotalQuantity">Everything reported in the window.</param>
/// <param name="UnbilledQuantity">The part not yet swept onto an invoice.</param>
/// <param name="EstimatedCharge">What the unbilled part would cost at the plan's current rate.</param>
public sealed record UsageSummaryDto(
    string Metric,
    decimal TotalQuantity,
    decimal UnbilledQuantity,
    decimal EstimatedCharge);

public interface IUsageService
{
    Task<UsageRecordDto> RecordAsync(RecordUsageRequest request, CancellationToken cancellationToken = default);

    Task<PagedResult<UsageRecordDto>> ListAsync(
        PageRequest page,
        Guid? subscriptionId = null,
        string? metric = null,
        bool? invoiced = null,
        CancellationToken cancellationToken = default);

    /// <summary>Current-period usage for a subscription, with what it would cost if invoiced now.</summary>
    Task<IReadOnlyList<UsageSummaryDto>> SummarizeCurrentPeriodAsync(
        Guid subscriptionId,
        CancellationToken cancellationToken = default);
}

public sealed class UsageService(IPayFlowDbContext db, ITenantContext tenant, IClock clock) : IUsageService
{
    public async Task<UsageRecordDto> RecordAsync(RecordUsageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var subscription = await db.Subscriptions
            .FirstOrDefaultAsync(x => x.Id == request.SubscriptionId, cancellationToken)
            ?? throw new NotFoundException(nameof(Subscription), request.SubscriptionId);

        if (!subscription.IsEntitled)
        {
            throw new DomainException($"Subscription {subscription.Id} is {subscription.Status} and cannot accrue usage.");
        }

        // De-duplicate before constructing: metering agents retry on timeout, and a
        // replayed report would otherwise become a second charge.
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var key = request.IdempotencyKey.Trim();
            var existing = await db.UsageRecords.FirstOrDefaultAsync(x => x.IdempotencyKey == key, cancellationToken);
            if (existing is not null)
            {
                return Map(existing);
            }
        }

        var record = new UsageRecord(
            tenant.TenantId,
            subscription.Id,
            subscription.CustomerId,
            request.Metric ?? "",
            request.Quantity,
            request.RecordedAt ?? clock.UtcNow,
            request.IdempotencyKey);

        db.UsageRecords.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return Map(record);
    }

    public async Task<PagedResult<UsageRecordDto>> ListAsync(
        PageRequest page,
        Guid? subscriptionId = null,
        string? metric = null,
        bool? invoiced = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.UsageRecords.AsNoTracking().AsQueryable();

        if (subscriptionId is { } id)
        {
            query = query.Where(x => x.SubscriptionId == id);
        }

        if (!string.IsNullOrWhiteSpace(metric))
        {
            var normalized = metric.Trim().ToLowerInvariant();
            query = query.Where(x => x.Metric == normalized);
        }

        if (invoiced is { } wasInvoiced)
        {
            query = wasInvoiced ? query.Where(x => x.InvoiceId != null) : query.Where(x => x.InvoiceId == null);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.RecordedAt)
            .ThenBy(x => x.Id)
            .Skip(page.Skip)
            .Take(page.Take)
            .ToListAsync(cancellationToken);

        return new PagedResult<UsageRecordDto>([.. items.Select(Map)], total, page.Page, page.Take);
    }

    public async Task<IReadOnlyList<UsageSummaryDto>> SummarizeCurrentPeriodAsync(
        Guid subscriptionId,
        CancellationToken cancellationToken = default)
    {
        var subscription = await db.Subscriptions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == subscriptionId, cancellationToken)
            ?? throw new NotFoundException(nameof(Subscription), subscriptionId);

        var plan = await db.Plans.AsNoTracking()
            .Include(x => x.MeteredRates)
            .FirstOrDefaultAsync(x => x.Id == subscription.PlanId, cancellationToken)
            ?? throw new NotFoundException(nameof(Plan), subscription.PlanId);

        var start = subscription.CurrentPeriodStart;
        var end = subscription.CurrentPeriodEnd;

        var records = await db.UsageRecords.AsNoTracking()
            .Where(x => x.SubscriptionId == subscriptionId && x.RecordedAt >= start && x.RecordedAt < end)
            .ToListAsync(cancellationToken);

        return
        [
            .. records
                .GroupBy(x => x.Metric, StringComparer.Ordinal)
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(group =>
                {
                    var unbilled = group.Where(x => !x.IsInvoiced).Sum(x => x.Quantity);
                    var rate = plan.RateFor(group.Key);
                    var charge = rate?.PriceFor(unbilled) ?? Money.Zero(plan.Currency);
                    return new UsageSummaryDto(group.Key, group.Sum(x => x.Quantity), unbilled, charge.Amount);
                }),
        ];
    }

    internal static UsageRecordDto Map(UsageRecord record) => new(
        record.Id,
        record.SubscriptionId,
        record.CustomerId,
        record.Metric,
        record.Quantity,
        record.RecordedAt,
        record.IsInvoiced,
        record.InvoiceId);
}
