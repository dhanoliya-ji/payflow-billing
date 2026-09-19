using PayFlow.Domain.Common;

namespace PayFlow.Domain.Payments;

/// <summary>
/// When to retry a failed collection, and when to stop.
/// <para>
/// Most involuntary churn is not customers leaving - it is expired cards and temporary
/// declines. Retrying on a spread-out schedule recovers a large share of it, which is
/// why the retry offsets are policy rather than a hard-coded constant: a tenant selling
/// a $9 consumer plan and one selling a $9,000 enterprise plan should not chase a
/// failure the same way.
/// </para>
/// <para>
/// The offsets deliberately widen (1, 3, 5, 7 days). Retrying a decline minutes later
/// mostly reproduces the decline and, on some card networks, counts against the
/// merchant's retry allowance.
/// </para>
/// </summary>
public sealed record DunningPolicy
{
    /// <summary>Offsets from the first failure at which to retry, in order.</summary>
    public required IReadOnlyList<TimeSpan> RetryOffsets { get; init; }

    /// <summary>Days after the invoice is issued before payment is expected.</summary>
    public required TimeSpan PaymentTerms { get; init; }

    /// <summary>
    /// What happens to the subscription when the retries run out: written off and the
    /// subscription expired, or left past due for a human to chase.
    /// </summary>
    public bool ExpireSubscriptionOnExhaustion { get; init; } = true;

    /// <summary>
    /// The default: due on issue, then four retries over a week. Roughly the industry
    /// shape, and the one the analytics notebook's recovery curve is calibrated against.
    /// </summary>
    public static DunningPolicy Default { get; } = new()
    {
        PaymentTerms = TimeSpan.Zero,
        RetryOffsets =
        [
            TimeSpan.FromDays(1),
            TimeSpan.FromDays(3),
            TimeSpan.FromDays(5),
            TimeSpan.FromDays(7),
        ],
    };

    /// <summary>A gentler schedule for higher-value invoices paid by invoice terms rather than card.</summary>
    public static DunningPolicy NetThirty { get; } = new()
    {
        PaymentTerms = TimeSpan.FromDays(30),
        RetryOffsets =
        [
            TimeSpan.FromDays(3),
            TimeSpan.FromDays(7),
            TimeSpan.FromDays(14),
        ],
        ExpireSubscriptionOnExhaustion = false,
    };

    /// <summary>Total attempts allowed: the first charge plus every retry.</summary>
    public int MaxAttempts => RetryOffsets.Count + 1;

    /// <summary>
    /// When to try again after <paramref name="attemptsSoFar"/> failures, or null when the
    /// schedule is exhausted and the invoice should be written off.
    /// </summary>
    /// <param name="attemptsSoFar">Failed attempts already recorded, including the one just made.</param>
    /// <param name="failedAt">When the most recent attempt failed.</param>
    public DateTimeOffset? NextAttemptAfter(int attemptsSoFar, DateTimeOffset failedAt)
    {
        if (attemptsSoFar < 1)
        {
            throw new DomainValidationException(nameof(attemptsSoFar), "At least one attempt must have been made.");
        }

        var index = attemptsSoFar - 1;
        return index < RetryOffsets.Count ? failedAt + RetryOffsets[index] : null;
    }

    /// <summary>True once no retries remain.</summary>
    public bool IsExhausted(int attemptsSoFar) => attemptsSoFar >= MaxAttempts;

    /// <summary>Validates that the schedule is usable before it is applied to real invoices.</summary>
    public DunningPolicy Validate()
    {
        if (RetryOffsets.Count == 0)
        {
            throw new DomainValidationException(nameof(RetryOffsets), "A dunning policy needs at least one retry.");
        }

        if (RetryOffsets.Any(x => x <= TimeSpan.Zero))
        {
            throw new DomainValidationException(nameof(RetryOffsets), "Retry offsets must be positive.");
        }

        if (PaymentTerms < TimeSpan.Zero)
        {
            throw new DomainValidationException(nameof(PaymentTerms), "Payment terms cannot be negative.");
        }

        return this;
    }
}
