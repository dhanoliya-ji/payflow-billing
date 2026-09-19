using Microsoft.EntityFrameworkCore;
using PayFlow.Domain;
using PayFlow.Infrastructure;

namespace PayFlow.Workers;

public sealed class BillingWorker(ILogger<BillingWorker> logger, IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PayFlowDbContext>();
                var now = DateTimeOffset.UtcNow;

                var dueSubscriptions = await db.Subscriptions
                    .Where(x => x.Status == SubscriptionStatus.Active || x.Status == SubscriptionStatus.Trialing || x.Status == SubscriptionStatus.PastDue)
                    .ToListAsync(stoppingToken);

                foreach (var subscription in dueSubscriptions)
                {
                    if (subscription.Status == SubscriptionStatus.Trialing && subscription.CurrentPeriodEnd <= now)
                    {
                        subscription.MarkTrialExpired();
                        logger.LogInformation("Trial expired for subscription {SubscriptionId}", subscription.Id);
                        continue;
                    }

                    if (subscription.CurrentPeriodEnd <= now)
                    {
                        if (subscription.Status == SubscriptionStatus.PastDue)
                        {
                            subscription.Expire();
                            logger.LogInformation("Subscription {SubscriptionId} expired", subscription.Id);
                        }
                        else
                        {
                            subscription.MarkPastDue();
                            logger.LogInformation("Subscription {SubscriptionId} marked past due", subscription.Id);
                        }
                    }
                }

                var staleInvoices = await db.Invoices
                    .Where(x => x.Status == Subscription.InvoiceStatus.Open && x.PeriodEnd.AddDays(7) <= now)
                    .ToListAsync(stoppingToken);

                foreach (var invoice in staleInvoices)
                {
                    invoice.MarkPastDue();
                    logger.LogInformation("Invoice {InvoiceId} marked past due", invoice.Id);
                }

                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Billing worker encountered an error while checking subscription health.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
