using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayFlow.Application.Abstractions;
using PayFlow.Infrastructure.Persistence;

namespace PayFlow.Workers;

/// <summary>
/// Drains the transactional outbox.
/// <para>
/// PayFlow ships no message broker, so "delivery" here is a structured log line per
/// event. That is deliberately the whole integration: the value of the outbox is that
/// the event was recorded atomically with the state change, and swapping this loop for a
/// real publisher is a change to one method rather than to the billing path.
/// </para>
/// <para>
/// Delivery is at-least-once. A crash between publishing and marking the row processed
/// re-delivers on restart, so consumers must be idempotent - which is why every event
/// carries the id of the thing it happened to.
/// </para>
/// </summary>
public sealed class OutboxDispatcherWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> options,
    ILogger<OutboxDispatcherWorker> logger) : BackgroundService
{
    /// <summary>Rows per pass. Bounded so one backlog cannot monopolise the loop.</summary>
    private const int BatchSize = 200;

    /// <summary>
    /// Attempts before a message is left alone. A message that has failed this many times
    /// is a poison message; continuing to retry it forever starves everything behind it.
    /// </summary>
    private const int MaxAttempts = 10;

    private readonly WorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_options.StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.OutboxInterval);

        do
        {
            try
            {
                await DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox dispatch failed; will retry on the next tick");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PayFlowDbContext>();

        // The outbox spans tenants by design - it is infrastructure, not tenant data -
        // and OutboxMessage carries no query filter, so no IgnoreQueryFilters is needed.
        var pending = await db.OutboxMessages
            .Where(x => x.ProcessedAt == null && x.AttemptCount < MaxAttempts)
            .OrderBy(x => x.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var delivered = 0;

        foreach (var message in pending)
        {
            try
            {
                logger.LogInformation(
                    "Domain event {EventType} for tenant {TenantId} occurred at {OccurredAt:u}: {Payload}",
                    message.EventType,
                    message.TenantId,
                    message.OccurredAt,
                    message.Payload);

                message.MarkProcessed(clock.UtcNow);
                delivered++;
            }
            catch (Exception ex)
            {
                message.MarkFailed(ex.Message);
                logger.LogWarning(
                    ex,
                    "Failed to dispatch outbox message {MessageId} ({EventType}), attempt {Attempt}",
                    message.Id,
                    message.EventType,
                    message.AttemptCount);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogDebug("Dispatched {Delivered} of {Total} outbox messages", delivered, pending.Count);
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
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
