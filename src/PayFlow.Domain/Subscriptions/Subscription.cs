using PayFlow.Domain.Billing;
using PayFlow.Domain.Common;
using PayFlow.Domain.Events;
using PayFlow.Domain.Plans;

namespace PayFlow.Domain.Subscriptions;

/// <summary>
/// A customer's ongoing commitment to a plan, and the state machine that governs it.
/// <para>
/// All transitions funnel through <see cref="Transition"/>, which consults a single
/// table of legal moves. Nothing outside this class sets <see cref="Status"/>, so an
/// illegal move - reactivating a cancelled subscription, pausing an expired one -
/// throws instead of quietly corrupting the ledger.
/// </para>
/// </summary>
public sealed class Subscription : Entity
{
    private Subscription()
    {
    }

    public Subscription(
        TenantId tenantId,
        Guid customerId,
        Plan plan,
        DateTimeOffset startsAt,
        int quantity = 1,
        bool withTrial = false)
        : base(tenantId)
    {
        ArgumentNullException.ThrowIfNull(plan);

        CustomerId = Guard.NotEmpty(customerId);
        PlanId = plan.Id;
        Quantity = Guard.PositiveCount(quantity);
        Interval = plan.Interval;
        Currency = plan.Currency;

        if (startsAt == default)
        {
            throw new DomainValidationException(nameof(startsAt), "A start date is required.");
        }

        var trialDays = withTrial ? plan.TrialDays : 0;
        if (withTrial && trialDays == 0)
        {
            throw new DomainException($"Plan {plan.Code} does not offer a trial.");
        }

        if (trialDays > 0)
        {
            Status = SubscriptionStatus.Trialing;
            TrialEndsAt = startsAt.AddDays(trialDays);

            // The first paid period begins when the trial ends, so a trial never
            // produces an invoice for time the customer was not charged for.
            CurrentPeriod = new BillingPeriod(startsAt, TrialEndsAt.Value);
        }
        else
        {
            Status = SubscriptionStatus.Active;
            CurrentPeriod = BillingPeriod.Starting(startsAt, Interval);
        }

        StartedAt = startsAt;
        Raise(new SubscriptionCreated(TenantId, Id, CustomerId, PlanId, Status, startsAt));
    }

    public Guid CustomerId { get; private set; }

    public Guid PlanId { get; private set; }

    /// <summary>Seat count. Always 1 for flat-rate and metered plans; the multiplier for per-seat plans.</summary>
    public int Quantity { get; private set; } = 1;

    public BillingInterval Interval { get; private set; }

    public string Currency { get; private set; } = Money.DefaultCurrency;

    public SubscriptionStatus Status { get; private set; }

    public BillingPeriod CurrentPeriod { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? TrialEndsAt { get; private set; }

    /// <summary>
    /// When set, the subscription runs to the end of the paid period and then cancels.
    /// This is what "cancel" means to most customers: they keep what they paid for.
    /// </summary>
    public bool CancelAtPeriodEnd { get; private set; }

    public DateTimeOffset? CanceledAt { get; private set; }

    /// <summary>When the subscription actually stopped serving. Set on reaching a terminal state.</summary>
    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>When the subscription first went past due; cleared on recovery. Drives the dunning clock.</summary>
    public DateTimeOffset? PastDueSince { get; private set; }

    /// <summary>Consecutive renewals that ended in unpaid invoices. Reset by a successful payment.</summary>
    public int ConsecutiveFailedPayments { get; private set; }

    public DateTimeOffset CurrentPeriodStart => CurrentPeriod.Start;

    public DateTimeOffset CurrentPeriodEnd => CurrentPeriod.End;

    public bool IsInTrial => Status == SubscriptionStatus.Trialing;

    public bool IsEntitled => Status.IsEntitled();

    /// <summary>Recurring charge for one period at the current seat count.</summary>
    public Money PeriodPrice(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsurePlanMatches(plan);

        return plan.PricingModel == PricingModel.PerSeat
            ? plan.Price.Times(Quantity)
            : plan.Price;
    }

    /// <summary>Normalised monthly recurring revenue contributed by this subscription.</summary>
    public Money MonthlyRecurringRevenue(Plan plan)
    {
        if (!Status.IsBillable())
        {
            return Money.Zero(Currency);
        }

        var price = PeriodPrice(plan);
        return new Money(price.Amount * Interval.MonthlyFactor(), price.Currency);
    }

    public void Activate(DateTimeOffset at, string reason = "activated") =>
        Transition(SubscriptionStatus.Active, at, reason);

    public void Pause(DateTimeOffset at, string reason = "paused") =>
        Transition(SubscriptionStatus.Paused, at, reason);

    public void Resume(DateTimeOffset at) => Transition(SubscriptionStatus.Active, at, "resumed");

    /// <summary>Records that this period's invoice went unpaid and starts the dunning clock.</summary>
    public void MarkPastDue(DateTimeOffset at, string reason = "payment failed")
    {
        ConsecutiveFailedPayments++;
        PastDueSince ??= at;
        if (Status != SubscriptionStatus.PastDue)
        {
            Transition(SubscriptionStatus.PastDue, at, reason);
        }
    }

    /// <summary>A payment succeeded: clear the dunning clock and return to Active if recovering.</summary>
    public void MarkPaymentRecovered(DateTimeOffset at)
    {
        ConsecutiveFailedPayments = 0;
        PastDueSince = null;
        if (Status == SubscriptionStatus.PastDue)
        {
            Transition(SubscriptionStatus.Active, at, "payment recovered");
        }
    }

    /// <summary>
    /// Ends the subscription. With <paramref name="immediately"/> false - the default and
    /// the usual customer intent - the subscription is flagged and stays entitled until
    /// the paid period runs out, at which point the billing cycle finalises it.
    /// </summary>
    public void Cancel(DateTimeOffset at, bool immediately = false, string reason = "canceled by request")
    {
        if (Status.IsTerminal())
        {
            throw new DomainException($"Subscription is already {Status}.");
        }

        if (!immediately && CurrentPeriod.Contains(at))
        {
            CancelAtPeriodEnd = true;
            CanceledAt = at;
            return;
        }

        CancelAtPeriodEnd = false;
        CanceledAt = at;
        Transition(SubscriptionStatus.Canceled, at, reason);
    }

    /// <summary>Withdraws a pending end-of-period cancellation while the period is still running.</summary>
    public void ResumeCancellation(DateTimeOffset at)
    {
        if (!CancelAtPeriodEnd)
        {
            return;
        }

        if (Status.IsTerminal())
        {
            throw new DomainException($"Subscription is already {Status} and cannot be resumed.");
        }

        CancelAtPeriodEnd = false;
        CanceledAt = null;
        Raise(new SubscriptionStatusChanged(TenantId, Id, Status, Status, "cancellation withdrawn", at));
    }

    /// <summary>Ends the subscription without customer action - a lapsed trial, or dunning exhausted.</summary>
    public void Expire(DateTimeOffset at, string reason) =>
        Transition(SubscriptionStatus.Expired, at, reason);

    /// <summary>
    /// Advances to the next billing period. Called by the billing cycle once the current
    /// period has ended; the caller is responsible for issuing the invoice.
    /// <para>
    /// A trialing subscription converts to Active here and its first paid period starts
    /// at the trial end, so trial days are never billed.
    /// </para>
    /// </summary>
    public void Renew(DateTimeOffset at)
    {
        if (Status.IsTerminal())
        {
            throw new DomainException($"Cannot renew a {Status} subscription.");
        }

        if (Status == SubscriptionStatus.Paused)
        {
            throw new DomainException("Cannot renew a paused subscription; resume it first.");
        }

        if (!CurrentPeriod.HasEnded(at))
        {
            throw new DomainException($"Subscription period has not ended yet (ends {CurrentPeriod.End:u}).");
        }

        if (CancelAtPeriodEnd)
        {
            Transition(SubscriptionStatus.Canceled, at, "cancellation took effect at period end");
            return;
        }

        CurrentPeriod = new BillingPeriod(CurrentPeriod.End, Interval.Advance(CurrentPeriod.End));

        if (Status == SubscriptionStatus.Trialing)
        {
            Transition(SubscriptionStatus.Active, at, "trial converted");
        }

        Raise(new SubscriptionRenewed(TenantId, Id, CurrentPeriod.Start, CurrentPeriod.End, at));
    }

    /// <summary>
    /// Switches plans mid-period and returns what the switch is worth. The subscription
    /// keeps its current period boundaries so the customer's anniversary does not move;
    /// the caller turns the returned credit and charge into invoice lines.
    /// </summary>
    public ProrationResult ChangePlan(Plan currentPlan, Plan newPlan, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(currentPlan);
        ArgumentNullException.ThrowIfNull(newPlan);
        EnsurePlanMatches(currentPlan);

        if (Status.IsTerminal())
        {
            throw new DomainException($"Cannot change the plan of a {Status} subscription.");
        }

        if (newPlan.Id == PlanId)
        {
            throw new DomainException("The subscription is already on that plan.");
        }

        if (!string.Equals(newPlan.Currency, Currency, StringComparison.Ordinal))
        {
            throw new DomainException($"Cannot move a {Currency} subscription to a {newPlan.Currency} plan.");
        }

        var oldPrice = PeriodPrice(currentPlan);
        var newQuantity = newPlan.PricingModel == PricingModel.PerSeat ? Quantity : 1;
        var newPrice = newPlan.PricingModel == PricingModel.PerSeat
            ? newPlan.Price.Times(newQuantity)
            : newPlan.Price;

        var proration = Proration.Calculate(CurrentPeriod, oldPrice, newPrice, at);

        var fromPlanId = PlanId;
        PlanId = newPlan.Id;
        Quantity = newQuantity;

        // The cadence follows the new plan from the next renewal onward; the current
        // period keeps its existing end date so the customer is not billed twice for
        // overlapping time.
        Interval = newPlan.Interval;

        Raise(new SubscriptionPlanChanged(
            TenantId,
            Id,
            fromPlanId,
            newPlan.Id,
            proration.Credit.Amount,
            proration.Charge.Amount,
            Currency,
            at));

        return proration;
    }

    /// <summary>
    /// Changes the seat count on a per-seat plan, prorating the difference over the rest
    /// of the period.
    /// </summary>
    public ProrationResult ChangeQuantity(Plan plan, int quantity, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsurePlanMatches(plan);
        Guard.PositiveCount(quantity);

        if (Status.IsTerminal())
        {
            throw new DomainException($"Cannot change the seat count of a {Status} subscription.");
        }

        if (plan.PricingModel != PricingModel.PerSeat)
        {
            throw new DomainException(
                $"Plan {plan.Code} is {plan.PricingModel}; seat counts only apply to per-seat plans.");
        }

        if (quantity == Quantity)
        {
            return new ProrationResult(Money.Zero(Currency), Money.Zero(Currency), 0m);
        }

        var oldPrice = plan.Price.Times(Quantity);
        var newPrice = plan.Price.Times(quantity);
        var proration = Proration.Calculate(CurrentPeriod, oldPrice, newPrice, at);

        var previous = Quantity;
        Quantity = quantity;
        Raise(new SubscriptionQuantityChanged(TenantId, Id, previous, quantity, at));

        return proration;
    }

    /// <summary>
    /// The complete transition table. Read it as "from this state, these are the only
    /// states reachable". Terminal states have no outgoing edges by construction.
    /// </summary>
    public static bool CanTransition(SubscriptionStatus from, SubscriptionStatus to) => from switch
    {
        SubscriptionStatus.Trialing => to is SubscriptionStatus.Active
            or SubscriptionStatus.Paused
            or SubscriptionStatus.Canceled
            or SubscriptionStatus.Expired,

        SubscriptionStatus.Active => to is SubscriptionStatus.PastDue
            or SubscriptionStatus.Paused
            or SubscriptionStatus.Canceled
            or SubscriptionStatus.Expired,

        SubscriptionStatus.PastDue => to is SubscriptionStatus.Active
            or SubscriptionStatus.Paused
            or SubscriptionStatus.Canceled
            or SubscriptionStatus.Expired,

        SubscriptionStatus.Paused => to is SubscriptionStatus.Active
            or SubscriptionStatus.Canceled
            or SubscriptionStatus.Expired,

        SubscriptionStatus.Canceled or SubscriptionStatus.Expired => false,

        _ => false,
    };

    private void EnsurePlanMatches(Plan plan)
    {
        if (plan.Id != PlanId)
        {
            throw new DomainException($"Plan {plan.Code} is not the plan this subscription is on.");
        }
    }

    private void Transition(SubscriptionStatus next, DateTimeOffset at, string reason)
    {
        if (Status == next)
        {
            return;
        }

        if (!CanTransition(Status, next))
        {
            throw new DomainException($"Cannot transition a subscription from {Status} to {next}.");
        }

        var previous = Status;
        Status = next;

        if (next.IsTerminal())
        {
            EndedAt = at;
            CancelAtPeriodEnd = false;
            if (next == SubscriptionStatus.Canceled)
            {
                CanceledAt ??= at;
            }
        }

        if (next == SubscriptionStatus.Active)
        {
            PastDueSince = null;
        }

        Raise(new SubscriptionStatusChanged(TenantId, Id, previous, next, reason, at));
    }
}
