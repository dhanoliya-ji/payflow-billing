namespace PayFlow.Domain.Common;

/// <summary>
/// Identifies the tenant that owns a row. Every persisted aggregate carries one, and
/// <c>PayFlowDbContext</c> turns it into a global query filter so a missing
/// <c>WHERE tenant_id = …</c> cannot leak data across tenants.
/// </summary>
public readonly record struct TenantId(Guid Value)
{
    public static TenantId New() => new(Guid.NewGuid());

    public bool IsEmpty => Value == Guid.Empty;

    public static TenantId Parse(string value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? new TenantId(parsed)
            : throw new ArgumentException($"'{value}' is not a valid tenant id.", nameof(value));

    public static bool TryParse(string? value, out TenantId tenantId)
    {
        if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty)
        {
            tenantId = new TenantId(parsed);
            return true;
        }

        tenantId = default;
        return false;
    }

    public override string ToString() => Value.ToString("N");
}
