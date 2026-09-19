using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayFlow.Application.Abstractions;

namespace PayFlow.Infrastructure.Payments;

/// <summary>Tunables for <see cref="SimulatedPaymentGateway"/>.</summary>
public sealed class PaymentGatewayOptions
{
    public const string SectionName = "PaymentGateway";

    /// <summary>Share of first attempts that succeed, 0..1.</summary>
    public double FirstAttemptSuccessRate { get; set; } = 0.92;

    /// <summary>
    /// Share of retries that succeed. Lower than the first attempt because the easy
    /// declines - a momentary network fault, a card that was briefly over its limit -
    /// have already been filtered out by the first attempt succeeding.
    /// </summary>
    public double RetrySuccessRate { get; set; } = 0.35;

    /// <summary>
    /// Share of declines that are permanent (expired, blocked or stolen card). These are
    /// not worth retrying and the dunning engine writes them off immediately.
    /// </summary>
    public double PermanentDeclineShare { get; set; } = 0.25;

    /// <summary>Simulated round-trip latency, so local timings are not unrealistically instant.</summary>
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Seed for the deterministic outcome function. A fixed seed makes a whole simulated
    /// billing history reproducible, which is what lets the analytics notebook be
    /// re-run and compared.
    /// </summary>
    public int Seed { get; set; } = 20260920;
}

/// <summary>
/// A payment processor simulator.
/// <para>
/// PayFlow integrates no real processor, so this is the only gateway it ships with, and
/// it is built to behave like one where it matters rather than to always succeed:
/// </para>
/// <list type="bullet">
/// <item>
/// Outcomes are a deterministic function of the idempotency key, so replaying a request
/// returns the original result and a captured charge is never captured twice - the
/// property that makes the worker safe to crash and restart mid-charge.
/// </item>
/// <item>
/// Declines carry realistic reason codes and are split into retriable and permanent, so
/// the dunning engine's branching is actually exercised.
/// </item>
/// <item>
/// Retries succeed at a lower rate than first attempts, which is what produces a
/// recovery curve in the analytics rather than a straight line.
/// </item>
/// </list>
/// <para>
/// Token prefixes force an outcome for tests: <c>pm_decline_</c> always declines
/// retriably, <c>pm_expired_</c> always declines permanently, <c>pm_ok_</c> always succeeds.
/// </para>
/// </summary>
public sealed class SimulatedPaymentGateway(
    IOptions<PaymentGatewayOptions> options,
    ILogger<SimulatedPaymentGateway> logger) : IPaymentGateway
{
    private static readonly (string Code, string Message)[] RetriableDeclines =
    [
        ("insufficient_funds", "The card has insufficient funds."),
        ("do_not_honor", "The issuing bank declined the charge without a reason."),
        ("processing_error", "The processor could not complete the transaction."),
        ("try_again_later", "The issuer is temporarily unavailable."),
    ];

    private static readonly (string Code, string Message)[] PermanentDeclines =
    [
        ("card_expired", "The card has expired."),
        ("lost_or_stolen_card", "The card has been reported lost or stolen."),
        ("incorrect_cvc", "The security code is incorrect."),
        ("card_not_supported", "The card does not support this type of purchase."),
    ];

    /// <summary>
    /// Captures already made, keyed by idempotency key. Real gateways keep this
    /// server-side for 24 hours; keeping it here is what makes a replayed request return
    /// the original reference instead of charging again.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _captures = new(StringComparer.Ordinal);

    private readonly PaymentGatewayOptions _options = options.Value;

    public string Name => "simulated";

    public async Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken cancellationToken = default)
    {
        if (_options.Latency > TimeSpan.Zero)
        {
            await Task.Delay(_options.Latency, cancellationToken);
        }

        if (_captures.TryGetValue(request.IdempotencyKey, out var existing))
        {
            logger.LogInformation(
                "Replaying captured charge {Reference} for idempotency key {Key}",
                existing,
                request.IdempotencyKey);

            return ChargeResult.Success(existing);
        }

        var outcome = Decide(request);

        if (outcome.Succeeded)
        {
            var reference = $"sim_ch_{Hash(request.IdempotencyKey)[..20]}";
            _captures[request.IdempotencyKey] = reference;

            logger.LogInformation(
                "Captured {Amount} for invoice {InvoiceId} on attempt {Attempt}",
                request.Amount,
                request.InvoiceId,
                request.AttemptNumber);

            return ChargeResult.Success(reference);
        }

        logger.LogInformation(
            "Declined {Amount} for invoice {InvoiceId} on attempt {Attempt}: {Code}",
            request.Amount,
            request.InvoiceId,
            request.AttemptNumber,
            outcome.Code);

        return ChargeResult.Declined(outcome.Code!, outcome.Message!, outcome.Retriable);
    }

    public async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default)
    {
        if (_options.Latency > TimeSpan.Zero)
        {
            await Task.Delay(_options.Latency, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(request.Reference))
        {
            return RefundResult.Rejected("unknown_charge", "No gateway reference was supplied.");
        }

        return RefundResult.Success($"sim_re_{Hash(request.IdempotencyKey)[..20]}");
    }

    private (bool Succeeded, string? Code, string? Message, bool Retriable) Decide(ChargeRequest request)
    {
        var token = request.PaymentMethodToken;

        if (token.StartsWith("pm_ok_", StringComparison.Ordinal))
        {
            return (true, null, null, false);
        }

        if (token.StartsWith("pm_decline_", StringComparison.Ordinal))
        {
            var forced = RetriableDeclines[0];
            return (false, forced.Code, forced.Message, true);
        }

        if (token.StartsWith("pm_expired_", StringComparison.Ordinal))
        {
            var forced = PermanentDeclines[0];
            return (false, forced.Code, forced.Message, false);
        }

        // A uniform 0..1 draw derived from the key, not from a shared Random: the same
        // request always gets the same answer, and concurrent calls do not interfere.
        var roll = UnitInterval(request.IdempotencyKey, _options.Seed);

        var threshold = request.AttemptNumber <= 1
            ? _options.FirstAttemptSuccessRate
            : _options.RetrySuccessRate;

        if (roll < threshold)
        {
            return (true, null, null, false);
        }

        // Second independent draw picks the decline reason, so the reason mix does not
        // correlate with how close the first draw was to the success threshold.
        var reasonRoll = UnitInterval(request.IdempotencyKey, _options.Seed + 1);
        var permanent = reasonRoll < _options.PermanentDeclineShare;

        var pool = permanent ? PermanentDeclines : RetriableDeclines;
        var index = (int)(UnitInterval(request.IdempotencyKey, _options.Seed + 2) * pool.Length);
        var decline = pool[Math.Clamp(index, 0, pool.Length - 1)];

        return (false, decline.Code, decline.Message, !permanent);
    }

    /// <summary>Maps a key and seed onto a uniform value in [0, 1).</summary>
    private static double UnitInterval(string key, int seed)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{key}"));
        var value = BitConverter.ToUInt32(bytes, 0);
        return value / (double)uint.MaxValue;
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
