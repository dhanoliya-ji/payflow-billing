using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Common;
using PayFlow.Domain.Customers;
using PayFlow.Domain.Invoicing;
using PayFlow.Domain.Payments;
using PayFlow.Domain.Subscriptions;

namespace PayFlow.Application.Payments;

public sealed record PaymentDto(
    Guid Id,
    Guid InvoiceId,
    Guid CustomerId,
    decimal Amount,
    decimal RefundedAmount,
    string Currency,
    PaymentStatus Status,
    string Reference,
    string? FailureCode,
    string? FailureMessage,
    int AttemptNumber,
    DateTimeOffset ProcessedAt);

/// <param name="Payment">The attempt that was recorded, successful or not.</param>
/// <param name="Succeeded">Whether funds were captured.</param>
/// <param name="InvoiceStatus">The invoice's status after the attempt.</param>
/// <param name="NextAttemptAt">When dunning will retry, or null when it has given up.</param>
/// <param name="DunningExhausted">True when the invoice was written off as uncollectible.</param>
public sealed record CollectionResult(
    PaymentDto Payment,
    bool Succeeded,
    InvoiceStatus InvoiceStatus,
    DateTimeOffset? NextAttemptAt,
    bool DunningExhausted);

public sealed record RefundPaymentRequest(decimal? Amount = null, string? Reason = null);

public interface IPaymentService
{
    /// <summary>Attempts to collect an invoice through the gateway and records the outcome.</summary>
    Task<CollectionResult> CollectAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    Task<PaymentDto> RefundAsync(Guid paymentId, RefundPaymentRequest request, CancellationToken cancellationToken = default);

    Task<PagedResult<PaymentDto>> ListAsync(
        PageRequest page,
        Guid? invoiceId = null,
        PaymentStatus? status = null,
        CancellationToken cancellationToken = default);
}

public sealed class PaymentService(
    IPayFlowDbContext db,
    IClock clock,
    IPaymentGateway gateway,
    DunningPolicy dunningPolicy) : IPaymentService
{
    public Task<CollectionResult> CollectAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            var invoice = await db.Invoices.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == invoiceId, ct)
                ?? throw new NotFoundException(nameof(Invoice), invoiceId);

            if (!invoice.Status.IsPayable())
            {
                throw new DomainException($"Invoice {invoice.Number?.ToString() ?? invoice.Id.ToString()} is {invoice.Status} and is not collectible.");
            }

            var customer = await db.Customers.FirstOrDefaultAsync(x => x.Id == invoice.CustomerId, ct)
                ?? throw new NotFoundException(nameof(Customer), invoice.CustomerId);

            if (!customer.HasPaymentMethod)
            {
                throw new DomainException($"Customer {customer.Email} has no payment method on file.");
            }

            var now = clock.UtcNow;
            var attemptNumber = invoice.AttemptCount + 1;
            var balance = invoice.Balance;

            // The key is derived from the invoice and attempt number rather than random,
            // so a worker that crashes after charging but before committing re-sends the
            // same key on restart and the gateway returns the original capture instead of
            // charging the card twice.
            var request = new ChargeRequest(
                invoice.TenantId,
                invoice.Id,
                customer.PaymentMethodToken!,
                balance,
                $"inv-{invoice.Id:N}-attempt-{attemptNumber}",
                attemptNumber);

            var result = await gateway.ChargeAsync(request, ct);

            var subscription = invoice.SubscriptionId is { } subscriptionId
                ? await db.Subscriptions.FirstOrDefaultAsync(x => x.Id == subscriptionId, ct)
                : null;

            Payment payment;
            DateTimeOffset? nextAttemptAt = null;
            var exhausted = false;

            if (result.Succeeded)
            {
                payment = Payment.Succeeded(
                    invoice.TenantId, invoice.Id, invoice.CustomerId, balance,
                    result.Reference ?? $"gw_{Guid.NewGuid():N}", now, attemptNumber);

                invoice.ApplyPayment(balance, now);
                subscription?.MarkPaymentRecovered(now);
            }
            else
            {
                payment = Payment.Failed(
                    invoice.TenantId, invoice.Id, invoice.CustomerId, balance,
                    result.FailureCode ?? "declined",
                    result.FailureMessage ?? "The payment was declined.",
                    now, attemptNumber, result.Reference);

                // A non-retriable decline - an expired or blocked card - will fail the
                // same way on every retry. Burning the remaining schedule on it only
                // delays the write-off and annoys the card network.
                nextAttemptAt = result.IsRetriable
                    ? dunningPolicy.NextAttemptAfter(attemptNumber, now)
                    : null;

                invoice.RecordFailedAttempt(payment.FailureCode!, now, nextAttemptAt);
                subscription?.MarkPastDue(now, payment.FailureCode!);

                if (nextAttemptAt is null)
                {
                    exhausted = true;
                    invoice.MarkUncollectible(
                        now,
                        result.IsRetriable ? "dunning schedule exhausted" : $"unrecoverable decline: {payment.FailureCode}");

                    if (dunningPolicy.ExpireSubscriptionOnExhaustion && subscription is not null
                        && !subscription.Status.IsTerminal())
                    {
                        subscription.Expire(now, "dunning exhausted");
                    }
                }
            }

            db.Payments.Add(payment);
            await db.SaveChangesAsync(ct);

            return new CollectionResult(Map(payment), result.Succeeded, invoice.Status, nextAttemptAt, exhausted);
        }, cancellationToken);

    public Task<PaymentDto> RefundAsync(Guid paymentId, RefundPaymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return db.InTransactionAsync(async ct =>
        {
            var payment = await db.Payments.FirstOrDefaultAsync(x => x.Id == paymentId, ct)
                ?? throw new NotFoundException(nameof(Payment), paymentId);

            var amount = request.Amount is { } value
                ? new Money(value, payment.Amount.Currency)
                : payment.Amount - payment.RefundedAmount;

            if (!amount.IsPositive)
            {
                throw new DomainException("There is nothing left to refund on this payment.");
            }

            var result = await gateway.RefundAsync(
                new RefundRequest(payment.TenantId, payment.Reference, amount, $"rfnd-{payment.Id:N}-{amount.Amount}"),
                ct);

            if (!result.Succeeded)
            {
                throw new DomainException(
                    $"The gateway rejected the refund: {result.FailureMessage ?? result.FailureCode ?? "unknown reason"}.");
            }

            var now = clock.UtcNow;
            payment.Refund(amount, now);

            var invoice = await db.Invoices.FirstOrDefaultAsync(x => x.Id == payment.InvoiceId, ct);
            invoice?.ReversePayment(amount, now);

            await db.SaveChangesAsync(ct);
            return Map(payment);
        }, cancellationToken);
    }

    public async Task<PagedResult<PaymentDto>> ListAsync(
        PageRequest page,
        Guid? invoiceId = null,
        PaymentStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.Payments.AsNoTracking().AsQueryable();

        if (invoiceId is { } id)
        {
            query = query.Where(x => x.InvoiceId == id);
        }

        if (status is { } s)
        {
            query = query.Where(x => x.Status == s);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.ProcessedAt)
            .ThenBy(x => x.Id)
            .Skip(page.Skip)
            .Take(page.Take)
            .ToListAsync(cancellationToken);

        return new PagedResult<PaymentDto>([.. items.Select(Map)], total, page.Page, page.Take);
    }

    internal static PaymentDto Map(Payment payment) => new(
        payment.Id,
        payment.InvoiceId,
        payment.CustomerId,
        payment.Amount.Amount,
        payment.RefundedAmount.Amount,
        payment.Amount.Currency,
        payment.Status,
        payment.Reference,
        payment.FailureCode,
        payment.FailureMessage,
        payment.AttemptNumber,
        payment.ProcessedAt);
}
