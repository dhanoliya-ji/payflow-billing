using PayFlow.Domain.Events;

namespace PayFlow.Domain.Common;

/// <summary>
/// Base for every tenant-scoped aggregate: identity, the owning tenant, a creation
/// stamp, and the buffer of domain events raised since the entity was loaded.
/// </summary>
public abstract class Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected Entity(TenantId tenantId)
    {
        TenantId = Guard.Tenant(tenantId);
        Id = Guid.NewGuid();
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Constructor used by EF Core materialization only.</summary>
    protected Entity()
    {
    }

    public Guid Id { get; protected set; }

    public TenantId TenantId { get; protected set; }

    public DateTimeOffset CreatedAt { get; protected set; }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public override bool Equals(object? obj) =>
        obj is Entity other && other.GetType() == GetType() && other.Id == Id && Id != Guid.Empty;

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
