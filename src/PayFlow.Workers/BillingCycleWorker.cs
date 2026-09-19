using Microsoft.Extensions.Options;
using PayFlow.Application.Billing;
using PayFlow.Domain.Common;

namespace PayFlow.Workers;

/// <summary>
/// Advances subscriptions whose billing period has ended, issues the invoices for the
/// periods that closed, and collects them.
/// <para>
/// This replaces the original worker, which marked every Active subscription PastDue the
/// moment its period ended and never renewed it, never invoiced it and never charged it.
/// The effect was that a healthy paying subscription degraded to PastDue and then Expired
/// on its own renewal date.
/// </para>
/// </summary>
public sealed class BillingCycleWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> options,
    ILogger<BillingCycleWorker> logger)
    : TenantScopedWorker(scopeFactory, logger, options.Value.BillingCycleInterval, options.Value.StartupDelay)
{
    protected override string WorkName => "Billing cycle";

    protected override async Task ExecuteForTenantAsync(
        IServiceProvider scopedServices,
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        var cycle = scopedServices.GetRequiredService<IBillingCycleService>();
        var report = await cycle.RunAsync(cancellationToken);

        if (!report.DidWork)
        {
            return;
        }

        logger.LogInformation(
            "Billing cycle for {TenantId}: renewed {Renewed}, converted {Converted} trials, " +
            "ended {Ended}, issued {Issued} invoices, collected {Collected}, failed {Failed}, wrote off {WrittenOff}",
            tenantId,
            report.SubscriptionsRenewed,
            report.TrialsConverted,
            report.SubscriptionsEnded,
            report.InvoicesIssued,
            report.PaymentsCollected,
            report.PaymentsFailed,
            report.InvoicesWrittenOff);

        foreach (var error in report.Errors)
        {
            logger.LogWarning("Billing cycle error for {TenantId}: {Error}", tenantId, error);
        }
    }
}

/// <summary>
/// Retries invoices whose dunning schedule has come due, and moves invoices past their
/// due date into dunning.
/// <para>
/// Separate from the billing cycle and on a slower interval: renewals are time-critical
/// and cheap, whereas a dunning sweep re-hits the payment gateway and there is no benefit
/// to retrying a declined card every five minutes.
/// </para>
/// </summary>
public sealed class DunningWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> options,
    ILogger<DunningWorker> logger)
    : TenantScopedWorker(scopeFactory, logger, options.Value.DunningInterval, options.Value.StartupDelay)
{
    protected override string WorkName => "Dunning sweep";

    protected override async Task ExecuteForTenantAsync(
        IServiceProvider scopedServices,
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        var cycle = scopedServices.GetRequiredService<IBillingCycleService>();
        var report = await cycle.RunDunningAsync(cancellationToken);

        if (!report.DidWork)
        {
            return;
        }

        logger.LogInformation(
            "Dunning for {TenantId}: recovered {Collected}, failed {Failed}, wrote off {WrittenOff}",
            tenantId,
            report.PaymentsCollected,
            report.PaymentsFailed,
            report.InvoicesWrittenOff);
    }
}
