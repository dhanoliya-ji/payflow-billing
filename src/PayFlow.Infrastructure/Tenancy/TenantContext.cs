using PayFlow.Application.Abstractions;
using PayFlow.Domain.Common;

namespace PayFlow.Infrastructure.Tenancy;

/// <summary>
/// Holds the tenant for the current request or job. Scoped, set once by the
/// authentication middleware in the web host or explicitly by a worker.
/// </summary>
public sealed class TenantContext : ITenantContext, ITenantContextSetter
{
    private TenantId _tenantId;

    public TenantId TenantId => IsResolved
        ? _tenantId
        : throw new InvalidOperationException(
            "No tenant has been resolved for this scope. The request was not authenticated, " +
            "or a background job started without calling SetTenant.");

    public bool IsResolved { get; private set; }

    public void SetTenant(TenantId tenantId)
    {
        if (tenantId.IsEmpty)
        {
            throw new ArgumentException("A tenant id is required.", nameof(tenantId));
        }

        _tenantId = tenantId;
        IsResolved = true;
    }
}
