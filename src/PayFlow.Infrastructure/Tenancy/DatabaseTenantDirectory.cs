using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Common;
using PayFlow.Infrastructure.Persistence;

namespace PayFlow.Infrastructure.Tenancy;

/// <summary>
/// Derives the tenant list from the data itself rather than from configuration.
/// <para>
/// A tenant added through the API starts being billed on the next cycle with no
/// redeploy, and a tenant removed from configuration does not silently stop being
/// billed while its subscriptions are still live.
/// </para>
/// <para>
/// This is one of the two deliberate uses of <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/>
/// in the codebase: the query has to cross tenants precisely because it is enumerating them.
/// </para>
/// </summary>
public sealed class DatabaseTenantDirectory(PayFlowDbContext db) : ITenantDirectory
{
    public async Task<IReadOnlyList<TenantId>> ListActiveTenantsAsync(CancellationToken cancellationToken = default)
    {
        var tenantIds = await db.Subscriptions
            .IgnoreQueryFilters()
            .Select(x => x.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return tenantIds;
    }
}
