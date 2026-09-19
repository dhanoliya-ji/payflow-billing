using PayFlow.Domain.Common;

namespace PayFlow.Domain.Invoicing;

/// <summary>What a line represents. Kept on the line so revenue can be split by kind.</summary>
public enum InvoiceLineKind
{
    /// <summary>The plan's recurring charge for the period.</summary>
    Recurring,

    /// <summary>Metered usage priced by the plan's rates.</summary>
    Usage,

    /// <summary>Credit for the unused part of a period after a mid-cycle change. Negative.</summary>
    ProrationCredit,

    /// <summary>Charge for the remainder of a period on new terms after a mid-cycle change.</summary>
    ProrationCharge,

    /// <summary>A manual correction applied by the tenant. May be either sign.</summary>
    Adjustment,
}

/// <summary>
/// A single priced row on an invoice. Amount is computed here rather than supplied, so a
/// line can never disagree with its own quantity and unit price.
/// </summary>
public sealed class InvoiceLine : Entity
{
    private InvoiceLine()
    {
    }

    public InvoiceLine(
        TenantId tenantId,
        Guid invoiceId,
        string description,
        decimal quantity,
        Money unitPrice,
        InvoiceLineKind kind = InvoiceLineKind.Recurring)
        : base(tenantId)
    {
        InvoiceId = Guard.NotEmpty(invoiceId);
        Description = Guard.NotNullOrWhiteSpace(description);
        Kind = kind;

        if (quantity == 0m)
        {
            throw new DomainValidationException(nameof(quantity), "An invoice line needs a non-zero quantity.");
        }

        Quantity = quantity;

        // The unit price keeps its full precision - a $0.002 metered rate must survive -
        // while the line amount is rounded to minor units, because that is the figure
        // that lands on the invoice and gets charged.
        UnitPrice = unitPrice;
        Amount = unitPrice.Times(quantity).Round();

        // Credits are the only lines allowed to be negative, and they must be.
        if (kind == InvoiceLineKind.ProrationCredit && Amount.IsPositive)
        {
            throw new DomainValidationException(nameof(unitPrice), "A proration credit must be negative.");
        }

        if (kind is InvoiceLineKind.Recurring or InvoiceLineKind.Usage or InvoiceLineKind.ProrationCharge
            && Amount.IsNegative)
        {
            throw new DomainValidationException(nameof(unitPrice), $"A {kind} line cannot be negative.");
        }
    }

    public Guid InvoiceId { get; private set; }

    public string Description { get; private set; } = "";

    public decimal Quantity { get; private set; }

    public Money UnitPrice { get; private set; }

    /// <summary>Always <c>UnitPrice * Quantity</c>, rounded once at construction.</summary>
    public Money Amount { get; private set; }

    public InvoiceLineKind Kind { get; private set; }

    /// <summary>For usage lines, the metric that produced them. Null otherwise.</summary>
    public string? Metric { get; private set; }

    /// <summary>Creates a usage line and tags it with its metric.</summary>
    public static InvoiceLine ForUsage(
        TenantId tenantId,
        Guid invoiceId,
        string metric,
        string description,
        decimal quantity,
        Money unitPrice) =>
        new(tenantId, invoiceId, description, quantity, unitPrice, InvoiceLineKind.Usage)
        {
            Metric = metric,
        };
}
