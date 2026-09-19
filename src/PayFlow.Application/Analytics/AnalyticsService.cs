using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Invoicing;
using PayFlow.Domain.Payments;
using PayFlow.Domain.Plans;
using PayFlow.Domain.Subscriptions;

namespace PayFlow.Application.Analytics;

/// <param name="PlanCode">Plan the slice belongs to.</param>
/// <param name="Subscriptions">Billable subscriptions on that plan.</param>
/// <param name="MonthlyRecurringRevenue">Their normalised monthly value.</param>
public sealed record MrrByPlanDto(string PlanCode, int Subscriptions, decimal MonthlyRecurringRevenue);

/// <param name="MonthlyRecurringRevenue">Normalised monthly recurring revenue across billable subscriptions.</param>
/// <param name="AnnualRunRate">MRR times twelve. The headline number, not a forecast.</param>
/// <param name="ActiveSubscriptions">Subscriptions currently active.</param>
/// <param name="TrialingSubscriptions">Subscriptions in a trial, contributing no MRR yet.</param>
/// <param name="PastDueSubscriptions">Subscriptions with an unpaid invoice in dunning.</param>
/// <param name="AverageRevenuePerAccount">MRR divided by active subscriptions.</param>
public sealed record MrrSnapshotDto(
    string Currency,
    decimal MonthlyRecurringRevenue,
    decimal AnnualRunRate,
    int ActiveSubscriptions,
    int TrialingSubscriptions,
    int PastDueSubscriptions,
    decimal AverageRevenuePerAccount,
    IReadOnlyList<MrrByPlanDto> ByPlan);

/// <param name="Month">First day of the month, UTC.</param>
/// <param name="Collected">Funds captured that month, net of refunds.</param>
/// <param name="Invoiced">Value of invoices issued that month.</param>
/// <param name="WrittenOff">Value of invoices marked uncollectible that month.</param>
public sealed record RevenueByMonthDto(
    DateTimeOffset Month,
    decimal Collected,
    decimal Invoiced,
    decimal WrittenOff);

/// <param name="AttemptNumber">Which dunning attempt, starting at 1.</param>
/// <param name="Attempts">Collection attempts made at this position in the schedule.</param>
/// <param name="Recovered">Attempts that captured funds.</param>
/// <param name="RecoveryRate">Recovered divided by attempts, 0..1.</param>
/// <param name="AmountRecovered">Value captured at this attempt.</param>
public sealed record DunningFunnelStepDto(
    int AttemptNumber,
    int Attempts,
    int Recovered,
    double RecoveryRate,
    decimal AmountRecovered);

/// <param name="Bucket">Aging band, e.g. <c>1-30</c>.</param>
/// <param name="InvoiceCount">Unpaid invoices in the band.</param>
/// <param name="Balance">Outstanding value in the band.</param>
public sealed record InvoiceAgingBucketDto(string Bucket, int InvoiceCount, decimal Balance);

/// <param name="FailureCode">Gateway decline reason.</param>
/// <param name="Count">Attempts that failed with it.</param>
/// <param name="Share">Its share of all failures, 0..1.</param>
public sealed record DeclineReasonDto(string FailureCode, int Count, double Share);

/// <summary>Everything the dashboard and the <c>/v1/analytics</c> endpoints report.</summary>
public interface IAnalyticsService
{
    Task<MrrSnapshotDto> GetMrrSnapshotAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RevenueByMonthDto>> GetRevenueByMonthAsync(int months = 12, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DunningFunnelStepDto>> GetDunningFunnelAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InvoiceAgingBucketDto>> GetInvoiceAgingAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeclineReasonDto>> GetDeclineReasonsAsync(CancellationToken cancellationToken = default);
}

public sealed class AnalyticsService(IPayFlowDbContext db, IClock clock, ICacheStore cache) : IAnalyticsService
{
    /// <summary>
    /// Short enough that the dashboard stays honest, long enough that a page refresh
    /// does not re-aggregate every invoice in the tenant.
    /// </summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public Task<MrrSnapshotDto> GetMrrSnapshotAsync(CancellationToken cancellationToken = default) =>
        cache.GetOrCreateAsync("analytics:mrr", CacheDuration, ComputeMrrAsync, cancellationToken);

    private async Task<MrrSnapshotDto> ComputeMrrAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Subscriptions.AsNoTracking()
            .Join(
                db.Plans.AsNoTracking(),
                s => s.PlanId,
                p => p.Id,
                (s, p) => new
                {
                    s.Status,
                    s.Quantity,
                    s.Interval,
                    s.Currency,
                    p.Code,
                    p.PricingModel,
                    PlanAmount = p.Price.Amount,
                })
            .ToListAsync(cancellationToken);

        // Normalise every cadence onto a monthly figure up front, so the grouping below
        // is a plain sum and a weekly plan is not compared with a yearly one at face value.
        var normalized = rows
            .Select(x => new
            {
                x.Status,
                x.Currency,
                x.Code,
                MonthlyValue = (x.PricingModel == PricingModel.PerSeat ? x.PlanAmount * x.Quantity : x.PlanAmount)
                    * x.Interval.MonthlyFactor(),
            })
            .ToList();

        var currency = rows.Select(x => x.Currency).FirstOrDefault() ?? Domain.Common.Money.DefaultCurrency;

        var active = rows.Count(x => x.Status == SubscriptionStatus.Active);
        var trialing = rows.Count(x => x.Status == SubscriptionStatus.Trialing);
        var pastDue = rows.Count(x => x.Status == SubscriptionStatus.PastDue);

        // Trials contribute nothing until they convert. Counting them as MRR is the most
        // common way a SaaS dashboard ends up overstating revenue.
        var billable = normalized.Where(x => x.Status.IsBillable()).ToList();

        var byPlan = billable
            .GroupBy(x => x.Code, StringComparer.Ordinal)
            .Select(group => new MrrByPlanDto(
                group.Key,
                group.Count(),
                decimal.Round(group.Sum(x => x.MonthlyValue), 2)))
            .OrderByDescending(x => x.MonthlyRecurringRevenue)
            .ToList();

        var mrr = decimal.Round(byPlan.Sum(x => x.MonthlyRecurringRevenue), 2);

        return new MrrSnapshotDto(
            currency,
            mrr,
            decimal.Round(mrr * 12m, 2),
            active,
            trialing,
            pastDue,
            active == 0 ? 0m : decimal.Round(mrr / active, 2),
            byPlan);
    }

    public async Task<IReadOnlyList<RevenueByMonthDto>> GetRevenueByMonthAsync(int months = 12, CancellationToken cancellationToken = default)
    {
        var window = Math.Clamp(months, 1, 60);
        var now = clock.UtcNow;
        var from = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(-(window - 1));

        var payments = await db.Payments.AsNoTracking()
            .Where(x => x.ProcessedAt >= from && x.Status != PaymentStatus.Failed)
            .Select(x => new { x.ProcessedAt, Gross = x.Amount.Amount, Refunded = x.RefundedAmount.Amount })
            .ToListAsync(cancellationToken);

        var invoices = await db.Invoices.AsNoTracking()
            .Where(x => x.IssuedAt != null && x.IssuedAt >= from && x.Status != InvoiceStatus.Void)
            .Select(x => new { x.IssuedAt, Total = x.Total.Amount, x.Status })
            .ToListAsync(cancellationToken);

        var buckets = Enumerable.Range(0, window)
            .Select(offset => from.AddMonths(offset))
            .ToList();

        return
        [
            .. buckets.Select(month =>
            {
                var next = month.AddMonths(1);

                var collected = payments
                    .Where(x => x.ProcessedAt >= month && x.ProcessedAt < next)
                    .Sum(x => x.Gross - x.Refunded);

                var monthInvoices = invoices.Where(x => x.IssuedAt >= month && x.IssuedAt < next).ToList();

                return new RevenueByMonthDto(
                    month,
                    decimal.Round(collected, 2),
                    decimal.Round(monthInvoices.Sum(x => x.Total), 2),
                    decimal.Round(monthInvoices.Where(x => x.Status == InvoiceStatus.Uncollectible).Sum(x => x.Total), 2));
            }),
        ];
    }

    public async Task<IReadOnlyList<DunningFunnelStepDto>> GetDunningFunnelAsync(CancellationToken cancellationToken = default)
    {
        var attempts = await db.Payments.AsNoTracking()
            .Select(x => new { x.AttemptNumber, x.Status, Amount = x.Amount.Amount })
            .ToListAsync(cancellationToken);

        return
        [
            .. attempts
                .GroupBy(x => x.AttemptNumber)
                .OrderBy(x => x.Key)
                .Select(group =>
                {
                    var total = group.Count();
                    var recovered = group.Count(x => x.Status != PaymentStatus.Failed);
                    return new DunningFunnelStepDto(
                        group.Key,
                        total,
                        recovered,
                        total == 0 ? 0d : recovered / (double)total,
                        decimal.Round(group.Where(x => x.Status != PaymentStatus.Failed).Sum(x => x.Amount), 2));
                }),
        ];
    }

    public async Task<IReadOnlyList<InvoiceAgingBucketDto>> GetInvoiceAgingAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;

        var outstanding = await db.Invoices.AsNoTracking()
            .Where(x => x.Status == InvoiceStatus.Open || x.Status == InvoiceStatus.PastDue)
            .Select(x => new { x.DueAt, Total = x.Total.Amount, Paid = x.AmountPaid.Amount })
            .ToListAsync(cancellationToken);

        // Standard accounts-receivable aging bands, so the output lines up with what a
        // finance team already reads elsewhere.
        var bands = new (string Label, int From, int To)[]
        {
            ("current", int.MinValue, 0),
            ("1-30", 1, 30),
            ("31-60", 31, 60),
            ("61-90", 61, 90),
            ("90+", 91, int.MaxValue),
        };

        return
        [
            .. bands.Select(band =>
            {
                var matching = outstanding.Where(x =>
                {
                    var days = x.DueAt is { } due ? (int)(now - due).TotalDays : 0;
                    return days >= band.From && days <= band.To;
                }).ToList();

                return new InvoiceAgingBucketDto(
                    band.Label,
                    matching.Count,
                    decimal.Round(matching.Sum(x => x.Total - x.Paid), 2));
            }),
        ];
    }

    public async Task<IReadOnlyList<DeclineReasonDto>> GetDeclineReasonsAsync(CancellationToken cancellationToken = default)
    {
        var failures = await db.Payments.AsNoTracking()
            .Where(x => x.Status == PaymentStatus.Failed && x.FailureCode != null)
            .Select(x => x.FailureCode!)
            .ToListAsync(cancellationToken);

        if (failures.Count == 0)
        {
            return [];
        }

        return
        [
            .. failures
                .GroupBy(x => x, StringComparer.Ordinal)
                .Select(group => new DeclineReasonDto(group.Key, group.Count(), group.Count() / (double)failures.Count))
                .OrderByDescending(x => x.Count),
        ];
    }
}
