using PayFlow.Domain.Common;

namespace PayFlow.Infrastructure.Persistence;

/// <summary>
/// The last invoice number handed out for a tenant.
/// <para>
/// A row per tenant rather than one global database sequence, because invoice numbers
/// must be per-tenant and gap-free: a tenant whose invoices jump 41, 58, 73 because
/// other tenants consumed the numbers in between has an audit problem. Allocation takes
/// a row lock, which serialises concurrent issuers on that one row.
/// </para>
/// </summary>
public sealed class InvoiceSequence
{
    private InvoiceSequence()
    {
    }

    public InvoiceSequence(TenantId tenantId, long lastNumber = 0)
    {
        TenantId = tenantId;
        LastNumber = lastNumber;
    }

    public TenantId TenantId { get; private set; }

    public long LastNumber { get; private set; }

    public long Next()
    {
        LastNumber++;
        return LastNumber;
    }
}
