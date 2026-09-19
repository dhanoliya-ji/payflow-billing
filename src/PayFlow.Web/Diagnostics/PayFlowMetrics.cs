using System.Diagnostics.Metrics;

namespace PayFlow.Web.Diagnostics;

/// <summary>
/// The instruments PayFlow publishes.
/// <para>
/// Built on <see cref="System.Diagnostics.Metrics"/> rather than a vendor client, so the
/// same instruments feed the built-in <c>/metrics</c> exposition here and an OpenTelemetry
/// exporter later without any code in the billing path changing.
/// </para>
/// <para>
/// These are deliberately business counters, not just request counts. "HTTP 200s are
/// flat" tells you nothing when every card charge is being declined.
/// </para>
/// </summary>
public sealed class PayFlowMetrics : IDisposable
{
    public const string MeterName = "PayFlow";

    private readonly Meter _meter;

    public PayFlowMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        _meter = meterFactory.Create(MeterName);

        InvoicesIssued = _meter.CreateCounter<long>(
            "payflow.invoices.issued",
            unit: "{invoice}",
            description: "Invoices finalised.");

        PaymentsAttempted = _meter.CreateCounter<long>(
            "payflow.payments.attempted",
            unit: "{attempt}",
            description: "Collection attempts, tagged by outcome and attempt number.");

        AmountCollected = _meter.CreateCounter<double>(
            "payflow.payments.collected",
            unit: "{currency}",
            description: "Value captured, tagged by currency.");

        InvoicesWrittenOff = _meter.CreateCounter<long>(
            "payflow.invoices.written_off",
            unit: "{invoice}",
            description: "Invoices marked uncollectible after dunning was exhausted.");

        SubscriptionsRenewed = _meter.CreateCounter<long>(
            "payflow.subscriptions.renewed",
            unit: "{subscription}",
            description: "Subscriptions advanced into a new billing period.");

        BillingCycleDuration = _meter.CreateHistogram<double>(
            "payflow.billing_cycle.duration",
            unit: "ms",
            description: "Wall-clock time for one billing cycle run.");
    }

    public Counter<long> InvoicesIssued { get; }

    public Counter<long> PaymentsAttempted { get; }

    public Counter<double> AmountCollected { get; }

    public Counter<long> InvoicesWrittenOff { get; }

    public Counter<long> SubscriptionsRenewed { get; }

    public Histogram<double> BillingCycleDuration { get; }

    public void Dispose() => _meter.Dispose();
}
