using PayFlow.Application.Abstractions;
using PayFlow.Domain.Common;

namespace PayFlow.Workers;

/// <summary>
/// Options shared by the scheduled workers.
/// </summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Workers";

    /// <summary>How often the billing cycle runs.</summary>
    public TimeSpan BillingCycleInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often dunning retries are swept.</summary>
    public TimeSpan DunningInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How often the outbox is drained.</summary>
    public TimeSpan OutboxInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Delay before the first run, giving the web host time to apply migrations against
    /// a database both processes share.
    /// </summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Runs a unit of work once per tenant, on a fixed interval.
/// <para>
/// Every query in the application layer is tenant-filtered, and a background job has no
/// request to take a tenant from. This base class enumerates tenants and opens a fresh DI
/// scope per tenant with that tenant pinned, so the filters work in the workers exactly as
/// they do behind the API - rather than the workers needing a privileged, unfiltered path
/// into the data.
/// </para>
/// </summary>
public abstract class TenantScopedWorker(
    IServiceScopeFactory scopeFactory,
    ILogger logger,
    TimeSpan interval,
    TimeSpan startupDelay) : BackgroundService
{
    protected abstract string WorkName { get; }

    /// <summary>Does the work for one tenant. The scope is already pinned to that tenant.</summary>
    protected abstract Task ExecuteForTenantAsync(
        IServiceProvider scopedServices,
        TenantId tenantId,
        CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (startupDelay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(startupDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A worker that dies on an unexpected error stops billing silently until
                // someone notices. Log it and keep the loop alive.
                logger.LogError(ex, "{WorkName} failed; will retry on the next tick", WorkName);
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TenantId> tenants;

        using (var directoryScope = scopeFactory.CreateScope())
        {
            var directory = directoryScope.ServiceProvider.GetRequiredService<ITenantDirectory>();
            tenants = await directory.ListActiveTenantsAsync(cancellationToken);
        }

        if (tenants.Count == 0)
        {
            logger.LogDebug("{WorkName}: no tenants with data yet", WorkName);
            return;
        }

        foreach (var tenantId in tenants)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // One scope per tenant: a fresh DbContext with no change-tracked entities
            // carried over, so one tenant's failure cannot contaminate the next tenant's
            // unit of work.
            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().SetTenant(tenantId);

            using (logger.BeginScope(new Dictionary<string, object> { ["TenantId"] = tenantId.ToString() }))
            {
                try
                {
                    await ExecuteForTenantAsync(scope.ServiceProvider, tenantId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "{WorkName} failed for tenant {TenantId}", WorkName, tenantId);
                }
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
