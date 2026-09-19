using PayFlow.Domain.Common;
using PayFlow.Domain.Events;

namespace PayFlow.Domain.Invoicing;

/// <summary>
/// A bill for one billing period of one subscription, or a one-off charge.
/// <para>
/// An invoice is assembled as a <see cref="InvoiceStatus.Draft"/>, then finalised with
/// <see cref="Finalize"/>, which freezes the lines and sets a due date. Nothing may be
/// added after that: an invoice a customer has already been shown must not change under
/// them. Corrections go on the next invoice as an
/// <see cref="InvoiceLineKind.Adjustment"/>, or the invoice is voided and reissued.
/// </para>
/// </summary>
public sealed class Invoice : Entity
{
    private readonly List<InvoiceLine> _lines = [];

    private Invoice()
    {
    }

    public Invoice(
        TenantId tenantId,
        Guid customerId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        string currency = Money.DefaultCurrency,
        Guid? subscriptionId = null)
        : base(tenantId)
    {
        CustomerId = Guard.NotEmpty(customerId);
        SubscriptionId = subscriptionId;

        if (periodEnd <= periodStart)
        {
            throw new DomainValidationException(nameof(periodEnd), "Invoice period must end after it starts.");
        }

        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        Currency = Money.Zero(currency).Currency;
        Status = InvoiceStatus.Draft;
        Subtotal = Money.Zero(Currency);
        Total = Money.Zero(Currency);
        AmountPaid = Money.Zero(Currency);
    }

    public Guid CustomerId { get; private set; }

    /// <summary>The subscription this bills, when it bills one. Null for one-off invoices.</summary>
    public Guid? SubscriptionId { get; private set; }

    /// <summary>
    /// Human-facing sequential number, unique within a tenant, assigned at finalisation.
    /// Drafts have none: gaps in an invoice sequence are an accounting problem, so a
    /// number is only spent once the invoice is certain to exist.
    /// </summary>
    public long? Number { get; private set; }

    public string Currency { get; private set; } = Money.DefaultCurrency;

    public DateTimeOffset PeriodStart { get; private set; }

    public DateTimeOffset PeriodEnd { get; private set; }

    public DateTimeOffset? IssuedAt { get; private set; }

    /// <summary>Payment deadline. Dunning starts once this passes with a balance outstanding.</summary>
    public DateTimeOffset? DueAt { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public DateTimeOffset? VoidedAt { get; private set; }

    public InvoiceStatus Status { get; private set; }

    public Money Subtotal { get; private set; }

    public Money Total { get; private set; }

    /// <summary>Cumulative amount settled. Partial payments are supported; the invoice closes at full.</summary>
    public Money AmountPaid { get; private set; }

    /// <summary>Failed collection attempts so far. Drives the dunning schedule.</summary>
    public int AttemptCount { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    /// <summary>When dunning should try again. Null when no retry is scheduled.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines => _lines;

    /// <summary>Outstanding amount. Never negative: an overpayment shows as a zero balance.</summary>
    public Money Balance
    {
        get
        {
            if (Status is InvoiceStatus.Void or InvoiceStatus.Uncollectible)
            {
                return Money.Zero(Currency);
            }

            var outstanding = Total - AmountPaid;
            return outstanding.IsNegative ? Money.Zero(Currency) : outstanding;
        }
    }

    public bool IsFullySettled => !Balance.IsPositive;

    public bool IsOverdue(DateTimeOffset asOf) =>
        Status.IsPayable() && DueAt is { } due && asOf >= due;

    /// <summary>Whole days a payable invoice has been overdue, for aging reports. Zero when current.</summary>
    public int DaysOverdue(DateTimeOffset asOf) =>
        IsOverdue(asOf) ? (int)(asOf - DueAt!.Value).TotalDays : 0;

    public InvoiceLine AddLine(
        string description,
        decimal quantity,
        Money unitPrice,
        InvoiceLineKind kind = InvoiceLineKind.Recurring)
    {
        EnsureDraft();
        EnsureCurrency(unitPrice.Currency);

        var line = new InvoiceLine(TenantId, Id, description, quantity, unitPrice, kind);
        _lines.Add(line);
        Recalculate();
        return line;
    }

    public InvoiceLine AddUsageLine(string metric, string description, decimal quantity, Money unitPrice)
    {
        EnsureDraft();
        EnsureCurrency(unitPrice.Currency);

        var line = InvoiceLine.ForUsage(TenantId, Id, metric, description, quantity, unitPrice);
        _lines.Add(line);
        Recalculate();
        return line;
    }

    /// <summary>Adds a proration credit, stored as a negative one-unit line.</summary>
    public InvoiceLine AddProrationCredit(string description, Money amount)
    {
        var magnitude = amount.IsNegative ? amount : amount.Negate();
        return AddLine(description, 1m, magnitude, InvoiceLineKind.ProrationCredit);
    }

    public InvoiceLine AddProrationCharge(string description, Money amount) =>
        AddLine(description, 1m, amount, InvoiceLineKind.ProrationCharge);

    /// <summary>
    /// Finalises the invoice: assigns <paramref name="number"/>, freezes the lines, and
    /// sets the due date. A zero-total invoice - a trial period, or a downgrade fully
    /// covered by credit - is marked paid immediately rather than left for a gateway
    /// that would reject a zero charge.
    /// </summary>
    public void Finalize(long number, DateTimeOffset issuedAt, TimeSpan paymentTerms)
    {
        EnsureDraft();

        if (_lines.Count == 0)
        {
            throw new DomainException("An invoice must have at least one line before it can be issued.");
        }

        if (number <= 0)
        {
            throw new DomainValidationException(nameof(number), "Invoice number must be positive.");
        }

        Number = number;
        IssuedAt = issuedAt;
        DueAt = issuedAt + paymentTerms;
        SetStatus(InvoiceStatus.Open, issuedAt, "finalized");

        Raise(new InvoiceIssued(
            TenantId, Id, CustomerId, SubscriptionId, Total.Amount, Currency, DueAt.Value, issuedAt));

        if (!Total.IsPositive)
        {
            AmountPaid = Total;
            PaidAt = issuedAt;
            NextAttemptAt = null;
            SetStatus(InvoiceStatus.Paid, issuedAt, "zero-total invoice settled on issue");
            Raise(new InvoicePaid(TenantId, Id, CustomerId, Total.Amount, Currency, 0, issuedAt));
        }
    }

    /// <summary>
    /// Applies a settled amount. Closes the invoice once the balance reaches zero;
    /// a partial payment leaves it payable with a reduced balance.
    /// </summary>
    public void ApplyPayment(Money amount, DateTimeOffset at)
    {
        EnsureCurrency(amount.Currency);

        if (!amount.IsPositive)
        {
            throw new DomainValidationException(nameof(amount), "A payment must be greater than zero.");
        }

        if (Status.IsClosed())
        {
            throw new DomainException($"Invoice {Describe()} is {Status} and cannot take a payment.");
        }

        if (Status == InvoiceStatus.Draft)
        {
            throw new DomainException("A draft invoice must be finalized before it can be paid.");
        }

        AmountPaid += amount;
        LastAttemptAt = at;

        if (IsFullySettled)
        {
            PaidAt = at;
            NextAttemptAt = null;
            SetStatus(InvoiceStatus.Paid, at, "paid in full");
            Raise(new InvoicePaid(TenantId, Id, CustomerId, Total.Amount, Currency, AttemptCount, at));
        }
    }

    /// <summary>
    /// Records a failed collection attempt and, when the dunning schedule still has
    /// retries left, when to try next.
    /// </summary>
    public void RecordFailedAttempt(string failureCode, DateTimeOffset at, DateTimeOffset? nextAttemptAt)
    {
        if (!Status.IsPayable())
        {
            throw new DomainException($"Invoice {Describe()} is {Status} and is not collectible.");
        }

        AttemptCount++;
        LastAttemptAt = at;
        NextAttemptAt = nextAttemptAt;

        if (Status == InvoiceStatus.Open && DueAt is { } due && at >= due)
        {
            SetStatus(InvoiceStatus.PastDue, at, "payment failed after due date");
        }

        Raise(new InvoicePaymentFailed(
            TenantId, Id, CustomerId, AttemptCount, failureCode, nextAttemptAt, at));
    }

    /// <summary>Moves an unpaid invoice past its due date into dunning.</summary>
    public void MarkPastDue(DateTimeOffset at)
    {
        if (Status != InvoiceStatus.Open)
        {
            return;
        }

        SetStatus(InvoiceStatus.PastDue, at, "past due date with an outstanding balance");
    }

    /// <summary>Writes the balance off after dunning is exhausted. The record is retained.</summary>
    public void MarkUncollectible(DateTimeOffset at, string reason = "dunning exhausted")
    {
        if (Status.IsClosed())
        {
            throw new DomainException($"Invoice {Describe()} is already {Status}.");
        }

        NextAttemptAt = null;
        SetStatus(InvoiceStatus.Uncollectible, at, reason);
    }

    /// <summary>
    /// Cancels the invoice before payment. A paid invoice cannot be voided - that would
    /// erase money that actually moved; refund the payment instead.
    /// </summary>
    public void Void(DateTimeOffset at, string reason = "voided")
    {
        if (Status == InvoiceStatus.Paid)
        {
            throw new DomainException($"Invoice {Describe()} is paid; refund it rather than voiding it.");
        }

        if (Status == InvoiceStatus.Void)
        {
            return;
        }

        VoidedAt = at;
        NextAttemptAt = null;
        SetStatus(InvoiceStatus.Void, at, reason);
    }

    /// <summary>Reverses a settled amount after a refund, reopening the invoice.</summary>
    public void ReversePayment(Money amount, DateTimeOffset at)
    {
        EnsureCurrency(amount.Currency);

        if (Status is InvoiceStatus.Void or InvoiceStatus.Draft)
        {
            throw new DomainException($"Invoice {Describe()} is {Status}; there is nothing to reverse.");
        }

        if (amount > AmountPaid)
        {
            throw new DomainException("Cannot reverse more than has been paid.");
        }

        AmountPaid -= amount;
        PaidAt = null;

        if (!IsFullySettled)
        {
            var next = DueAt is { } due && at >= due ? InvoiceStatus.PastDue : InvoiceStatus.Open;
            SetStatus(next, at, "payment reversed");
        }
    }

    private void Recalculate()
    {
        // Summing already-rounded line amounts, so the total agrees with the lines a
        // customer can add up by hand. Rounding the raw sum instead can differ by a cent.
        Subtotal = Money.Sum(_lines.Select(x => x.Amount), Currency).Round();

        // No tax engine yet, so total tracks subtotal. Kept as a distinct property so
        // adding tax later does not change the meaning of any stored column.
        Total = Subtotal;
    }

    private void SetStatus(InvoiceStatus next, DateTimeOffset at, string reason)
    {
        if (Status == next)
        {
            return;
        }

        var previous = Status;
        Status = next;
        Raise(new InvoiceStatusChanged(TenantId, Id, previous, next, reason, at));
    }

    private void EnsureDraft()
    {
        if (Status != InvoiceStatus.Draft)
        {
            throw new DomainException(
                $"Invoice {Describe()} is {Status}; lines can only be added while it is a draft.");
        }
    }

    private void EnsureCurrency(string currency)
    {
        if (!string.Equals(currency, Currency, StringComparison.Ordinal))
        {
            throw new DomainException($"Invoice {Describe()} is denominated in {Currency}, not {currency}.");
        }
    }

    private string Describe() => Number?.ToString() ?? Id.ToString("N")[..8];
}
