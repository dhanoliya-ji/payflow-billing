namespace PayFlow.Domain;

public readonly record struct TenantId(Guid Value)
{
    public static TenantId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public enum SubscriptionStatus { Trialing, Active, PastDue, Paused, Canceled, Expired }
public enum BillingInterval { Monthly, Yearly }

public sealed class Customer
{
    private Customer() { }
    public Customer(TenantId tenantId, string email, string displayName)
    {
        if (tenantId.Value == Guid.Empty) throw new ArgumentException("Tenant is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) throw new ArgumentException("A valid email is required.", nameof(email));
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Display name is required.", nameof(displayName));
        Id = Guid.NewGuid(); TenantId = tenantId; Email = email.Trim(); DisplayName = displayName.Trim();
    }
    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public string Email { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
}

public sealed class Plan
{
    private Plan() { }
    public Plan(TenantId tenantId, string code, string name, decimal amount, BillingInterval interval)
    {
        if (tenantId.Value == Guid.Empty) throw new ArgumentException("Tenant is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Plan code and name are required.");
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        Id = Guid.NewGuid(); TenantId = tenantId; Code = code.Trim().ToUpperInvariant(); Name = name.Trim(); Amount = decimal.Round(amount, 2); Interval = interval;
    }
    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public decimal Amount { get; private set; }
    public BillingInterval Interval { get; private set; }
    public bool IsActive { get; private set; } = true;
}

public sealed class Subscription
{
    private Subscription() { }
    public Subscription(TenantId tenantId, Guid customerId, Guid planId, DateTimeOffset startsAt, BillingInterval interval = BillingInterval.Monthly, bool trial = false)
    {
        if (tenantId.Value == Guid.Empty) throw new ArgumentException("Tenant is required.", nameof(tenantId));
        if (customerId == Guid.Empty) throw new ArgumentException("Customer is required.", nameof(customerId));
        if (planId == Guid.Empty) throw new ArgumentException("Plan is required.", nameof(planId));
        if (startsAt == default) throw new ArgumentOutOfRangeException(nameof(startsAt));

        Id = Guid.NewGuid(); TenantId = tenantId; CustomerId = customerId; PlanId = planId; BillingInterval = interval;
        Status = trial ? SubscriptionStatus.Trialing : SubscriptionStatus.Active;
        CurrentPeriodStart = startsAt;
        CurrentPeriodEnd = BillingInterval == BillingInterval.Yearly ? startsAt.AddYears(1) : startsAt.AddMonths(1);
    }

    public sealed class UsageRecord
    {
        private UsageRecord() { }
        public UsageRecord(TenantId tenantId, Guid customerId, string metric, decimal quantity, DateTimeOffset recordedAt)
        {
            if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
            if (string.IsNullOrWhiteSpace(metric)) throw new ArgumentException("Metric is required.", nameof(metric));
            Id = Guid.NewGuid(); TenantId = tenantId; CustomerId = customerId; Metric = metric.Trim(); Quantity = quantity; RecordedAt = recordedAt;
        }
        public Guid Id { get; private set; }
        public TenantId TenantId { get; private set; }
        public Guid CustomerId { get; private set; }
        public string Metric { get; private set; } = "";
        public decimal Quantity { get; private set; }
        public DateTimeOffset RecordedAt { get; private set; }
        public bool Invoiced { get; private set; }
        public void MarkInvoiced() => Invoiced = true;
    }

    public enum InvoiceStatus { Draft, Open, Paid, Void, PastDue }

    public sealed class Invoice
    {
        private Invoice() { }
        public Invoice(TenantId tenantId, Guid customerId, DateTimeOffset periodStart, DateTimeOffset periodEnd)
        {
            if (tenantId.Value == Guid.Empty) throw new ArgumentException("Tenant is required.", nameof(tenantId));
            if (customerId == Guid.Empty) throw new ArgumentException("Customer is required.", nameof(customerId));
            Id = Guid.NewGuid(); TenantId = tenantId; CustomerId = customerId;
            PeriodStart = periodStart; PeriodEnd = periodEnd; Status = InvoiceStatus.Open; IssuedAt = DateTimeOffset.UtcNow;
        }
        public Guid Id { get; private set; }
        public TenantId TenantId { get; private set; }
        public Guid CustomerId { get; private set; }
        public DateTimeOffset PeriodStart { get; private set; }
        public DateTimeOffset PeriodEnd { get; private set; }
        public DateTimeOffset IssuedAt { get; private set; }
        public InvoiceStatus Status { get; private set; }
        public decimal Subtotal { get; private set; }
        public decimal Total { get; private set; }
        public List<InvoiceLine> Lines { get; private set; } = [];
        public void AddLine(string description, decimal quantity, decimal unitPrice)
        {
            if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("Description is required.", nameof(description));
            if (quantity <= 0 || unitPrice < 0) throw new ArgumentOutOfRangeException(nameof(quantity));
            Lines.Add(new InvoiceLine(TenantId, Id, description, quantity, unitPrice));
            Recalculate();
        }
        public void MarkPaid() { if (Status is InvoiceStatus.Void or InvoiceStatus.Paid) throw new InvalidOperationException("Paid or void invoice cannot be paid again."); Status = InvoiceStatus.Paid; }
        public void MarkPastDue() { if (Status == InvoiceStatus.Open) Status = InvoiceStatus.PastDue; }
        public void MarkOpen() { if (Status == InvoiceStatus.Void) throw new InvalidOperationException("Void invoice cannot be reopened."); Status = InvoiceStatus.Open; }
        public void Void()
        {
            if (Status == InvoiceStatus.Paid) throw new InvalidOperationException("Paid invoice cannot be voided.");
            Status = InvoiceStatus.Void;
        }
        private void Recalculate() { Subtotal = decimal.Round(Lines.Sum(x => x.Amount), 2); Total = Subtotal; }
    }

    public sealed class InvoiceLine
    {
        private InvoiceLine() { }
        public InvoiceLine(TenantId tenantId, Guid invoiceId, string description, decimal quantity, decimal unitPrice)
        {
            if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("Description is required.", nameof(description));
            Id = Guid.NewGuid(); TenantId = tenantId; InvoiceId = invoiceId; Description = description.Trim(); Quantity = quantity;
            UnitPrice = decimal.Round(unitPrice, 2); Amount = decimal.Round(quantity * unitPrice, 2);
        }
        public Guid Id { get; private set; }
        public TenantId TenantId { get; private set; }
        public Guid InvoiceId { get; private set; }
        public string Description { get; private set; } = "";
        public decimal Quantity { get; private set; }
        public decimal UnitPrice { get; private set; }
        public decimal Amount { get; private set; }
    }

    public enum PaymentStatus { Succeeded, Failed, Refunded }
    public sealed class Payment
    {
        private Payment() { }
        public Payment(TenantId tenantId, Guid invoiceId, decimal amount, string reference)
        {
            if (tenantId.Value == Guid.Empty) throw new ArgumentException("Tenant is required.", nameof(tenantId));
            if (invoiceId == Guid.Empty) throw new ArgumentException("Invoice is required.", nameof(invoiceId));
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
            Id = Guid.NewGuid(); TenantId = tenantId; InvoiceId = invoiceId; Amount = decimal.Round(amount, 2);
            Reference = string.IsNullOrWhiteSpace(reference) ? $"mock_{Id:N}" : reference.Trim(); Status = PaymentStatus.Succeeded; PaidAt = DateTimeOffset.UtcNow;
        }
        public Guid Id { get; private set; }
        public TenantId TenantId { get; private set; }
        public Guid InvoiceId { get; private set; }
        public decimal Amount { get; private set; }
        public string Reference { get; private set; } = "";
        public PaymentStatus Status { get; private set; }
        public DateTimeOffset PaidAt { get; private set; }
        public void MarkFailed() => Status = PaymentStatus.Failed;
        public void Refund() => Status = PaymentStatus.Refunded;
    }
    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid CustomerId { get; private set; }
    public Guid PlanId { get; private set; }
    public BillingInterval BillingInterval { get; private set; }
    public SubscriptionStatus Status { get; private set; }
    public DateTimeOffset CurrentPeriodStart { get; private set; }
    public DateTimeOffset CurrentPeriodEnd { get; private set; }
    public DateTimeOffset? CanceledAt { get; private set; }

    public void Activate() => Transition(SubscriptionStatus.Active);
    public void MarkPastDue() => Transition(SubscriptionStatus.PastDue);
    public void Pause() => Transition(SubscriptionStatus.Paused);
    public void MarkTrialExpired() => Transition(SubscriptionStatus.Expired);
    public void Cancel(DateTimeOffset at)
    {
        Transition(SubscriptionStatus.Canceled); CanceledAt = at;
    }
    public void Expire() => Transition(SubscriptionStatus.Expired);
    public void Renew(DateTimeOffset at)
    {
        if (Status is SubscriptionStatus.Canceled or SubscriptionStatus.Expired) throw new InvalidOperationException($"Cannot renew a {Status} subscription.");
        CurrentPeriodStart = CurrentPeriodEnd;
        CurrentPeriodEnd = BillingInterval == BillingInterval.Yearly ? CurrentPeriodStart.AddYears(1) : CurrentPeriodStart.AddMonths(1);
        if (Status == SubscriptionStatus.Trialing) Status = SubscriptionStatus.Active;
    }
    private void Transition(SubscriptionStatus next)
    {
        if (!CanTransition(Status, next)) throw new InvalidOperationException($"Cannot transition {Status} to {next}.");
        Status = next;
    }
    private static bool CanTransition(SubscriptionStatus from, SubscriptionStatus to) => from switch
    {
        SubscriptionStatus.Trialing => to is SubscriptionStatus.Active or SubscriptionStatus.Canceled or SubscriptionStatus.Expired,
        SubscriptionStatus.Active => to is SubscriptionStatus.PastDue or SubscriptionStatus.Paused or SubscriptionStatus.Canceled,
        SubscriptionStatus.PastDue => to is SubscriptionStatus.Active or SubscriptionStatus.Paused or SubscriptionStatus.Canceled or SubscriptionStatus.Expired,
        SubscriptionStatus.Paused => to is SubscriptionStatus.Active or SubscriptionStatus.Canceled,
        _ => false
    };
}
