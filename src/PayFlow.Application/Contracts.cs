using PayFlow.Domain;

namespace PayFlow.Application;

public sealed record SubscriptionSummary(Guid Id, string CustomerEmail, string PlanCode, SubscriptionStatus Status, DateTimeOffset PeriodEnd);
public interface ISubscriptionService
{
    Task<IReadOnlyList<SubscriptionSummary>> ListAsync(TenantId tenantId, CancellationToken cancellationToken = default);
}
