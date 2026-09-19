using System.Text.RegularExpressions;
using PayFlow.Domain.Common;

namespace PayFlow.Domain.Customers;

/// <summary>
/// The party an invoice is addressed to. A customer belongs to exactly one tenant and
/// may hold several subscriptions.
/// </summary>
public sealed partial class Customer : Entity
{
    private Customer()
    {
    }

    public Customer(
        TenantId tenantId,
        string email,
        string displayName,
        string currency = Money.DefaultCurrency,
        string? externalReference = null)
        : base(tenantId)
    {
        Email = NormalizeEmail(email);
        DisplayName = Guard.NotNullOrWhiteSpace(displayName);
        Currency = new Money(0m, currency).Currency;
        ExternalReference = string.IsNullOrWhiteSpace(externalReference) ? null : externalReference.Trim();
    }

    public string Email { get; private set; } = "";

    public string DisplayName { get; private set; } = "";

    /// <summary>
    /// Currency every invoice for this customer is denominated in. Held on the customer
    /// rather than derived per invoice so a customer cannot end up with a mixed-currency
    /// ledger that no balance query can total.
    /// </summary>
    public string Currency { get; private set; } = Money.DefaultCurrency;

    /// <summary>Caller-supplied id (CRM record, auth subject) for reconciliation. Not interpreted by PayFlow.</summary>
    public string? ExternalReference { get; private set; }

    /// <summary>
    /// Opaque token standing in for stored card details. PayFlow never holds a PAN;
    /// the gateway owns the instrument and hands back a token, which keeps this
    /// service out of PCI scope.
    /// </summary>
    public string? PaymentMethodToken { get; private set; }

    public bool IsArchived { get; private set; }

    public bool HasPaymentMethod => !string.IsNullOrWhiteSpace(PaymentMethodToken);

    public void AttachPaymentMethod(string token) =>
        PaymentMethodToken = Guard.NotNullOrWhiteSpace(token);

    public void DetachPaymentMethod() => PaymentMethodToken = null;

    public void ChangeEmail(string email) => Email = NormalizeEmail(email);

    public void Rename(string displayName) => DisplayName = Guard.NotNullOrWhiteSpace(displayName);

    /// <summary>
    /// Soft-deletes the customer. Hard deletion is deliberately not offered: invoices
    /// and payments must stay referentially intact for financial reporting and audit.
    /// </summary>
    public void Archive() => IsArchived = true;

    public void Restore() => IsArchived = false;

    private static string NormalizeEmail(string email)
    {
        var normalized = Guard.NotNullOrWhiteSpace(email).ToLowerInvariant();
        if (!EmailPattern().IsMatch(normalized))
        {
            throw new DomainValidationException(nameof(email), $"'{email}' is not a valid email address.");
        }

        return normalized;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();
}
