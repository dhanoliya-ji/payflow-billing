using PayFlow.Domain.Common;

namespace PayFlow.Domain.Payments;

/// <summary>Outcome of a single collection attempt. Persisted as text.</summary>
public enum PaymentStatus
{
    /// <summary>Submitted to the gateway, outcome not yet known.</summary>
    Pending,

    /// <summary>Funds captured.</summary>
    Succeeded,

    /// <summary>Declined or errored. <see cref="Payment.FailureCode"/> says why.</summary>
    Failed,

    /// <summary>Captured, then returned in part or in full.</summary>
    Refunded,
}

/// <summary>
/// One attempt to collect an invoice, successful or not.
/// <para>
/// Failed attempts are kept as rows rather than being discarded: the dunning funnel,
/// the decline-reason breakdown and any dispute all need the attempt history, and a
/// system that only stores successes cannot explain why revenue went missing.
/// </para>
/// </summary>
public sealed class Payment : Entity
{
    private Payment()
    {
    }

    private Payment(TenantId tenantId, Guid invoiceId, Guid customerId, Money amount, int attemptNumber)
        : base(tenantId)
    {
        InvoiceId = Guard.NotEmpty(invoiceId);
        CustomerId = Guard.NotEmpty(customerId);

        if (!amount.IsPositive)
        {
            throw new DomainValidationException(nameof(amount), "A payment amount must be greater than zero.");
        }

        Amount = amount;
        RefundedAmount = Money.Zero(amount.Currency);
        AttemptNumber = attemptNumber < 1 ? 1 : attemptNumber;
    }

    public Guid InvoiceId { get; private set; }

    public Guid CustomerId { get; private set; }

    public Money Amount { get; private set; }

    public Money RefundedAmount { get; private set; }

    public PaymentStatus Status { get; private set; }

    /// <summary>Gateway transaction id. The handle used to refund or to reconcile against a statement.</summary>
    public string Reference { get; private set; } = "";

    /// <summary>Machine-readable decline reason, e.g. <c>insufficient_funds</c>. Null on success.</summary>
    public string? FailureCode { get; private set; }

    public string? FailureMessage { get; private set; }

    /// <summary>Which dunning attempt this was, starting at 1.</summary>
    public int AttemptNumber { get; private set; }

    public DateTimeOffset ProcessedAt { get; private set; }

    public bool IsSuccessful => Status is PaymentStatus.Succeeded or PaymentStatus.Refunded;

    /// <summary>Amount that is still captured after any refunds.</summary>
    public Money NetAmount => Status == PaymentStatus.Failed ? Money.Zero(Amount.Currency) : Amount - RefundedAmount;

    public static Payment Succeeded(
        TenantId tenantId,
        Guid invoiceId,
        Guid customerId,
        Money amount,
        string reference,
        DateTimeOffset at,
        int attemptNumber = 1) =>
        new(tenantId, invoiceId, customerId, amount, attemptNumber)
        {
            Status = PaymentStatus.Succeeded,
            Reference = Guard.NotNullOrWhiteSpace(reference),
            ProcessedAt = at,
        };

    public static Payment Failed(
        TenantId tenantId,
        Guid invoiceId,
        Guid customerId,
        Money amount,
        string failureCode,
        string failureMessage,
        DateTimeOffset at,
        int attemptNumber = 1,
        string? reference = null) =>
        new(tenantId, invoiceId, customerId, amount, attemptNumber)
        {
            Status = PaymentStatus.Failed,
            FailureCode = Guard.NotNullOrWhiteSpace(failureCode),
            FailureMessage = Guard.NotNullOrWhiteSpace(failureMessage),
            Reference = string.IsNullOrWhiteSpace(reference) ? "" : reference.Trim(),
            ProcessedAt = at,
        };

    /// <summary>
    /// Returns part or all of a captured payment. Refunding more than was captured, or
    /// refunding a failed attempt, throws.
    /// </summary>
    public void Refund(Money amount, DateTimeOffset at)
    {
        if (Status == PaymentStatus.Failed)
        {
            throw new DomainException("A failed payment captured nothing and cannot be refunded.");
        }

        if (!string.Equals(amount.Currency, Amount.Currency, StringComparison.Ordinal))
        {
            throw new DomainException($"Cannot refund {amount.Currency} against a {Amount.Currency} payment.");
        }

        if (!amount.IsPositive)
        {
            throw new DomainValidationException(nameof(amount), "A refund must be greater than zero.");
        }

        var newTotal = RefundedAmount + amount;
        if (newTotal > Amount)
        {
            throw new DomainException($"Refunding {newTotal} would exceed the {Amount} captured.");
        }

        RefundedAmount = newTotal;
        Status = PaymentStatus.Refunded;
        RefundedAt = at;
    }

    public DateTimeOffset? RefundedAt { get; private set; }

    /// <summary>Refunds the entire captured amount.</summary>
    public void RefundInFull(DateTimeOffset at) => Refund(Amount - RefundedAmount, at);
}
