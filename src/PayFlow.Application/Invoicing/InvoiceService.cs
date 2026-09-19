using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Billing;
using PayFlow.Domain.Common;
using PayFlow.Domain.Invoicing;
using PayFlow.Domain.Payments;
using PayFlow.Domain.Plans;
using PayFlow.Domain.Subscriptions;

namespace PayFlow.Application.Invoicing;

public sealed record InvoiceLineDto(
    Guid Id,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal Amount,
    InvoiceLineKind Kind,
    string? Metric);

public sealed record InvoiceDto(
    Guid Id,
    long? Number,
    Guid CustomerId,
    Guid? SubscriptionId,
    InvoiceStatus Status,
    string Currency,
    decimal Subtotal,
    decimal Total,
    decimal AmountPaid,
    decimal Balance,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? DueAt,
    DateTimeOffset? PaidAt,
    int AttemptCount,
    DateTimeOffset? NextAttemptAt,
    int DaysOverdue,
    IReadOnlyList<InvoiceLineDto> Lines);

public sealed record GenerateInvoiceRequest(Guid SubscriptionId);

public interface IInvoiceService
{
    /// <summary>
    /// Bills the subscription's current period on demand. The background billing cycle
    /// does the same thing on a schedule; this exists for manual and test-driven runs.
    /// </summary>
    Task<InvoiceDto> GenerateForSubscriptionAsync(GenerateInvoiceRequest request, CancellationToken cancellationToken = default);

    Task<InvoiceDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<InvoiceDto>> ListAsync(
        PageRequest page,
        InvoiceStatus? status = null,
        Guid? customerId = null,
        CancellationToken cancellationToken = default);

    Task<InvoiceDto> VoidAsync(Guid id, string? reason = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Issuing an invoice for an arbitrary period, which only the billing cycle needs.
/// Split out from <see cref="IInvoiceService"/> so the cycle depends on the operation it
/// actually uses rather than downcasting to the implementation.
/// </summary>
public interface IInvoiceIssuer
{
    /// <summary>
    /// Builds, stores and finalises an invoice for <paramref name="period"/>, consuming
    /// the subscription's unbilled usage and pending proration adjustments. The caller
    /// owns the transaction and the save.
    /// </summary>
    Task<Invoice> IssueAsync(Subscription subscription, BillingPeriod period, CancellationToken cancellationToken = default);
}

public sealed class InvoiceService(
    IPayFlowDbContext db,
    IClock clock,
    IInvoiceNumberSequence numbers,
    DunningPolicy dunningPolicy) : IInvoiceService, IInvoiceIssuer
{
    public Task<InvoiceDto> GenerateForSubscriptionAsync(GenerateInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return db.InTransactionAsync(async ct =>
        {
            var subscription = await db.Subscriptions.FirstOrDefaultAsync(x => x.Id == request.SubscriptionId, ct)
                ?? throw new NotFoundException(nameof(Subscription), request.SubscriptionId);

            var invoice = await IssueAsync(subscription, subscription.CurrentPeriod, ct);
            await db.SaveChangesAsync(ct);
            return Map(invoice);
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Shared by the on-demand endpoint and the billing cycle worker so both produce
    /// identical invoices for the same inputs.
    /// </remarks>
    public async Task<Invoice> IssueAsync(
        Subscription subscription,
        BillingPeriod period,
        CancellationToken cancellationToken = default)
    {
        if (!subscription.Status.IsEntitled())
        {
            throw new DomainException($"Subscription {subscription.Id} is {subscription.Status} and cannot be invoiced.");
        }

        var plan = await db.Plans.Include(x => x.MeteredRates)
            .FirstOrDefaultAsync(x => x.Id == subscription.PlanId, cancellationToken)
            ?? throw new NotFoundException(nameof(Plan), subscription.PlanId);

        // An invoice already covering this period means someone billed it twice - a
        // retried worker run, or an operator clicking the button after the cycle ran.
        var alreadyBilled = await db.Invoices.AnyAsync(
            x => x.SubscriptionId == subscription.Id
                 && x.PeriodStart == period.Start
                 && x.Status != InvoiceStatus.Void,
            cancellationToken);

        if (alreadyBilled)
        {
            throw new DomainException(
                $"Subscription {subscription.Id} already has an invoice for the period starting {period.Start:u}.");
        }

        var unbilledUsage = plan.PricingModel == PricingModel.Metered
            ? await db.UsageRecords
                .Where(x => x.SubscriptionId == subscription.Id
                            && x.InvoiceId == null
                            && x.RecordedAt >= period.Start
                            && x.RecordedAt < period.End)
                .ToListAsync(cancellationToken)
            : [];

        var adjustments = await db.SubscriptionAdjustments
            .Where(x => x.SubscriptionId == subscription.Id && x.InvoiceId == null)
            .OrderBy(x => x.EffectiveAt)
            .ToListAsync(cancellationToken);

        var draft = BillingCalculator.Build(
            subscription,
            plan,
            period,
            unbilledUsage,
            adjustments.Select(x => x.ToPending()));

        var invoice = draft.Invoice;
        db.Invoices.Add(invoice);

        var now = clock.UtcNow;
        var number = await numbers.NextAsync(subscription.TenantId, cancellationToken);
        invoice.Finalize(number, now, dunningPolicy.PaymentTerms);

        foreach (var record in draft.ConsumedUsage)
        {
            record.MarkInvoiced(invoice.Id, now);
        }

        foreach (var adjustment in adjustments)
        {
            adjustment.MarkApplied(invoice.Id, now);
        }

        return invoice;
    }

    public async Task<InvoiceDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices.AsNoTracking()
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Invoice), id);

        return Map(invoice, clock.UtcNow);
    }

    public async Task<PagedResult<InvoiceDto>> ListAsync(
        PageRequest page,
        InvoiceStatus? status = null,
        Guid? customerId = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.Invoices.AsNoTracking().Include(x => x.Lines).AsQueryable();

        if (status is { } s)
        {
            query = query.Where(x => x.Status == s);
        }

        if (customerId is { } c)
        {
            query = query.Where(x => x.CustomerId == c);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.IssuedAt ?? x.CreatedAt)
            .ThenBy(x => x.Id)
            .Skip(page.Skip)
            .Take(page.Take)
            .ToListAsync(cancellationToken);

        var now = clock.UtcNow;
        return new PagedResult<InvoiceDto>([.. items.Select(x => Map(x, now))], total, page.Page, page.Take);
    }

    public Task<InvoiceDto> VoidAsync(Guid id, string? reason = null, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            var invoice = await db.Invoices.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException(nameof(Invoice), id);

            var now = clock.UtcNow;
            invoice.Void(now, reason ?? "voided");

            // Release everything the invoice consumed so a corrected invoice can pick it
            // up. Without this, voiding an invoice silently destroys billable usage.
            var usage = await db.UsageRecords.Where(x => x.InvoiceId == invoice.Id).ToListAsync(ct);
            foreach (var record in usage)
            {
                record.ReleaseFromInvoice();
            }

            var adjustments = await db.SubscriptionAdjustments.Where(x => x.InvoiceId == invoice.Id).ToListAsync(ct);
            foreach (var adjustment in adjustments)
            {
                adjustment.Release();
            }

            await db.SaveChangesAsync(ct);
            return Map(invoice, now);
        }, cancellationToken);

    internal static InvoiceDto Map(Invoice invoice, DateTimeOffset? asOf = null) => new(
        invoice.Id,
        invoice.Number,
        invoice.CustomerId,
        invoice.SubscriptionId,
        invoice.Status,
        invoice.Currency,
        invoice.Subtotal.Amount,
        invoice.Total.Amount,
        invoice.AmountPaid.Amount,
        invoice.Balance.Amount,
        invoice.PeriodStart,
        invoice.PeriodEnd,
        invoice.IssuedAt,
        invoice.DueAt,
        invoice.PaidAt,
        invoice.AttemptCount,
        invoice.NextAttemptAt,
        invoice.DaysOverdue(asOf ?? DateTimeOffset.UtcNow),
        [
            .. invoice.Lines.Select(line => new InvoiceLineDto(
                line.Id,
                line.Description,
                line.Quantity,
                line.UnitPrice.Amount,
                line.Amount.Amount,
                line.Kind,
                line.Metric)),
        ]);
}
