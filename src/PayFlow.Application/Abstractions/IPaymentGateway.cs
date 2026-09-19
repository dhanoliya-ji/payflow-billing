using PayFlow.Domain.Common;

namespace PayFlow.Application.Abstractions;

/// <summary>
/// The card processor, behind a port.
/// <para>
/// PayFlow never sees a card number. The customer holds an opaque
/// <c>PaymentMethodToken</c> issued by the gateway, and collection is "charge this token
/// this amount". That keeps the service out of PCI scope and makes the whole payment
/// path testable, because the only implementation shipped here is a simulator.
/// </para>
/// </summary>
public interface IPaymentGateway
{
    /// <summary>Human-readable name of the backing processor, surfaced on the health endpoint.</summary>
    string Name { get; }

    /// <summary>
    /// Attempts to collect <paramref name="amount"/> against <paramref name="request"/>'s
    /// payment method. Implementations must be safe to call twice with the same
    /// <see cref="ChargeRequest.IdempotencyKey"/> and must not throw for a decline -
    /// a declined card is an expected outcome, not an error.
    /// </summary>
    Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns funds from a previously captured charge.</summary>
    Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default);
}

/// <param name="TenantId">Tenant the charge belongs to.</param>
/// <param name="InvoiceId">Invoice being collected, used as the gateway's order reference.</param>
/// <param name="PaymentMethodToken">Opaque gateway token standing in for the stored card.</param>
/// <param name="Amount">Amount to capture.</param>
/// <param name="IdempotencyKey">Stable per-attempt key so a retried request cannot double-charge.</param>
/// <param name="AttemptNumber">Which dunning attempt this is, starting at 1.</param>
public readonly record struct ChargeRequest(
    TenantId TenantId,
    Guid InvoiceId,
    string PaymentMethodToken,
    Money Amount,
    string IdempotencyKey,
    int AttemptNumber);

/// <param name="Succeeded">Whether funds were captured.</param>
/// <param name="Reference">Gateway transaction id. Present on success, may be present on failure.</param>
/// <param name="FailureCode">Machine-readable decline reason. Null on success.</param>
/// <param name="FailureMessage">Human-readable decline reason. Null on success.</param>
/// <param name="IsRetriable">
/// Whether retrying later could plausibly succeed. An expired card is not retriable
/// without customer action; insufficient funds is. Dunning uses this to avoid burning
/// retries on declines that will never clear.
/// </param>
public readonly record struct ChargeResult(
    bool Succeeded,
    string? Reference,
    string? FailureCode,
    string? FailureMessage,
    bool IsRetriable)
{
    public static ChargeResult Success(string reference) => new(true, reference, null, null, false);

    public static ChargeResult Declined(string code, string message, bool retriable = true) =>
        new(false, null, code, message, retriable);
}

/// <param name="TenantId">Tenant the refund belongs to.</param>
/// <param name="Reference">Gateway transaction id of the original capture.</param>
/// <param name="Amount">Amount to return. May be less than the original capture.</param>
/// <param name="IdempotencyKey">Stable key so a retried refund does not return the money twice.</param>
public readonly record struct RefundRequest(
    TenantId TenantId,
    string Reference,
    Money Amount,
    string IdempotencyKey);

/// <param name="Succeeded">Whether the refund was accepted.</param>
/// <param name="Reference">Gateway reference for the refund itself.</param>
/// <param name="FailureCode">Reason the refund was rejected. Null on success.</param>
/// <param name="FailureMessage">Human-readable rejection reason. Null on success.</param>
public readonly record struct RefundResult(
    bool Succeeded,
    string? Reference,
    string? FailureCode,
    string? FailureMessage)
{
    public static RefundResult Success(string reference) => new(true, reference, null, null);

    public static RefundResult Rejected(string code, string message) => new(false, null, code, message);
}
