using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Invoicing;
using PayFlow.Application.Payments;
using PayFlow.Domain.Common;
using PayFlow.Domain.Invoicing;
using PayFlow.Domain.Payments;
using PayFlow.Domain.Subscriptions;

namespace PayFlow.Application.Billing;

/// <param name="SubscriptionsRenewed">Subscriptions advanced into a new billing period.</param>
/// <param name="TrialsConverted">Trials that ended and became paying subscriptions.</param>
/// <param name="SubscriptionsEnded">Subscriptions that reached a terminal state this run.</param>
/// <param name="InvoicesIssued">Invoices finalised this run.</param>
/// <param name="PaymentsCollected">Collection attempts that captured funds.</param>
/// <param name="PaymentsFailed">Collection attempts that were declined.</param>
/// <param name="InvoicesWrittenOff">Invoices marked uncollectible after dunning gave up.</param>
/// <param name="AmountCollected">Total captured, by currency.</param>
/// <param name="Errors">Per-subscription failures. One bad row must not stop the run.</param>
public sealed record BillingCycleReport(
    int SubscriptionsRenewed,
    int TrialsConverted,
    int SubscriptionsEnded,
    int InvoicesIssued,
    int PaymentsCollected,
    int PaymentsFailed,
    int InvoicesWrittenOff,
    IReadOnlyDictionary<string, decimal> AmountCollected,
    IReadOnlyList<string> Errors)
{
    public static BillingCycleReport Empty { get; } =
        new(0, 0, 0, 0, 0, 0, 0, new Dictionary<string, decimal>(StringComparer.Ordinal), []);

    public bool DidWork =>
        SubscriptionsRenewed + InvoicesIssued + PaymentsCollected + PaymentsFailed + InvoicesWrittenOff > 0;
}

public interface IBillingCycleService
{
    /// <summary>
    /// Advances every subscription whose period has ended, issues the invoices for the
    /// periods that closed, and collects them. Safe to call repeatedly: subscriptions
    /// whose period has not ended are skipped, and a period that already has a
    /// non-void invoice is not billed again.
    /// </summary>
    Task<BillingCycleReport> RunAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retries invoices whose dunning schedule has come due, and moves invoices past
    /// their due date into the past-due state.
    /// </summary>
    Task<BillingCycleReport> RunDunningAsync(CancellationToken cancellationToken = default);
}

public sealed class BillingCycleService(
    IPayFlowDbContext db,
    IClock clock,
    IInvoiceIssuer invoiceIssuer,
    IPaymentService payments,
    DunningPolicy dunningPolicy,
    ILogger<BillingCycleService> logger) : IBillingCycleService
{
    /// <summary>
    /// Bounds a single run so a backlog cannot hold a transaction open indefinitely. The
    /// worker calls the cycle again on its next tick and works through the rest.
    /// </summary>
    private const int BatchSize = 500;

    public async Task<BillingCycleReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var accumulator = new ReportAccumulator();

        var due = await db.Subscriptions
            .Where(x => x.Status != SubscriptionStatus.Canceled
                        && x.Status != SubscriptionStatus.Expired
                        && x.Status != SubscriptionStatus.Paused)
            .Where(x => x.CurrentPeriod.End <= now)
            .OrderBy(x => x.CurrentPeriod.End)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var subscription in due)
        {
            try
            {
                await AdvanceAsync(subscription, now, accumulator, cancellationToken);
            }
            catch (DomainException ex)
            {
                // One subscription in a bad state must not stall billing for every other
                // tenant. Record it, carry on, and surface the count in the report.
                logger.LogError(
                    ex,
                    "Billing cycle failed for subscription {SubscriptionId} of tenant {TenantId}",
                    subscription.Id,
                    subscription.TenantId);

                accumulator.Errors.Add($"{subscription.Id}: {ex.Message}");
            }
        }

        return accumulator.Build();
    }

    public async Task<BillingCycleReport> RunDunningAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var accumulator = new ReportAccumulator();

        // Invoices whose due date has passed but that nothing has chased yet.
        var newlyOverdue = await db.Invoices
            .Where(x => x.Status == InvoiceStatus.Open && x.DueAt != null && x.DueAt <= now)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var invoice in newlyOverdue)
        {
            invoice.MarkPastDue(now);

            // First failure has not happened yet, so schedule the first retry from now.
            if (invoice.NextAttemptAt is null && invoice.AttemptCount == 0)
            {
                invoice.RecordFailedAttempt("not_attempted", now, dunningPolicy.NextAttemptAfter(1, now));
                accumulator.PaymentsFailed++;
            }
        }

        if (newlyOverdue.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        var retryable = await db.Invoices
            .Where(x => (x.Status == InvoiceStatus.PastDue || x.Status == InvoiceStatus.Open)
                        && x.NextAttemptAt != null
                        && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt)
            .Take(BatchSize)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var invoiceId in retryable)
        {
            try
            {
                await CollectAsync(invoiceId, accumulator, cancellationToken);
            }
            catch (DomainException ex)
            {
                logger.LogError(ex, "Dunning retry failed for invoice {InvoiceId}", invoiceId);
                accumulator.Errors.Add($"{invoiceId}: {ex.Message}");
            }
        }

        return accumulator.Build();
    }

    private async Task AdvanceAsync(
        Subscription subscription,
        DateTimeOffset now,
        ReportAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        // Capture the period that just closed before Renew moves the window: that is the
        // period being billed. Billing is in arrears throughout, which is what lets a
        // metered plan invoice usage that is only known once the period is over.
        var closedPeriod = subscription.CurrentPeriod;
        var wasTrialing = subscription.IsInTrial;
        var wasCancelling = subscription.CancelAtPeriodEnd;

        Guid? issuedInvoiceId = null;

        await db.InTransactionAsync(async ct =>
        {
            // A trial period is free by definition, so it produces no invoice at all
            // rather than a zero-total one nobody needs to read.
            if (!wasTrialing)
            {
                var invoice = await invoiceIssuer.IssueAsync(subscription, closedPeriod, ct);
                issuedInvoiceId = invoice.Id;
                accumulator.InvoicesIssued++;
            }

            subscription.Renew(now);

            if (subscription.Status.IsTerminal())
            {
                accumulator.SubscriptionsEnded++;
            }
            else
            {
                accumulator.SubscriptionsRenewed++;
                if (wasTrialing)
                {
                    accumulator.TrialsConverted++;
                }
            }

            await db.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        logger.LogInformation(
            "Subscription {SubscriptionId} advanced past {PeriodEnd:u}; status {Status}{Cancelled}",
            subscription.Id,
            closedPeriod.End,
            subscription.Status,
            wasCancelling ? " (cancellation took effect)" : "");

        if (issuedInvoiceId is { } id)
        {
            await CollectAsync(id, accumulator, cancellationToken);
        }
    }

    private async Task CollectAsync(Guid invoiceId, ReportAccumulator accumulator, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == invoiceId, cancellationToken);

        if (invoice is null || !invoice.Status.IsPayable() || !invoice.Balance.IsPositive)
        {
            return;
        }

        var hasPaymentMethod = await db.Customers.AsNoTracking()
            .Where(x => x.Id == invoice.CustomerId)
            .Select(x => x.PaymentMethodToken != null)
            .FirstOrDefaultAsync(cancellationToken);

        if (!hasPaymentMethod)
        {
            // Nothing to charge against. Leave the invoice open for a manual payment
            // rather than recording a decline the customer did not cause.
            logger.LogWarning(
                "Invoice {InvoiceId} is payable but customer {CustomerId} has no payment method",
                invoice.Id,
                invoice.CustomerId);
            return;
        }

        var result = await payments.CollectAsync(invoiceId, cancellationToken);

        if (result.Succeeded)
        {
            accumulator.PaymentsCollected++;
            accumulator.Add(result.Payment.Currency, result.Payment.Amount);
        }
        else
        {
            accumulator.PaymentsFailed++;
            if (result.DunningExhausted)
            {
                accumulator.InvoicesWrittenOff++;
            }
        }
    }

    private sealed class ReportAccumulator
    {
        public int SubscriptionsRenewed { get; set; }

        public int TrialsConverted { get; set; }

        public int SubscriptionsEnded { get; set; }

        public int InvoicesIssued { get; set; }

        public int PaymentsCollected { get; set; }

        public int PaymentsFailed { get; set; }

        public int InvoicesWrittenOff { get; set; }

        public Dictionary<string, decimal> Collected { get; } = new(StringComparer.Ordinal);

        public List<string> Errors { get; } = [];

        public void Add(string currency, decimal amount) =>
            Collected[currency] = Collected.GetValueOrDefault(currency) + amount;

        public BillingCycleReport Build() => new(
            SubscriptionsRenewed,
            TrialsConverted,
            SubscriptionsEnded,
            InvoicesIssued,
            PaymentsCollected,
            PaymentsFailed,
            InvoicesWrittenOff,
            Collected,
            Errors);
    }
}
