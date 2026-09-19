using PayFlow.Domain.Common;

namespace PayFlow.Application.Abstractions;

/// <summary>
/// The current time, injected rather than read from <see cref="DateTimeOffset.UtcNow"/>.
/// Billing is almost entirely time-dependent - trials lapse, periods roll over, dunning
/// retries come due - and none of that is testable against a clock you cannot move.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// The tenant the current unit of work belongs to. Resolved from the request in the web
/// host and set explicitly in the workers.
/// </summary>
public interface ITenantContext
{
    TenantId TenantId { get; }

    bool IsResolved { get; }
}

/// <summary>Mutable counterpart of <see cref="ITenantContext"/>, set once per request or job.</summary>
public interface ITenantContextSetter
{
    void SetTenant(TenantId tenantId);
}

/// <summary>A key/value cache. Misses and backend outages must degrade to a miss, never throw.</summary>
public interface ICacheStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    Task SetAsync<T>(string key, T value, TimeSpan expiry, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Returns the cached value, or computes, stores and returns it on a miss.</summary>
    Task<T> GetOrCreateAsync<T>(
        string key,
        TimeSpan expiry,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Allocates gap-free, per-tenant invoice numbers. Separate from the DbContext because
/// it must serialise across concurrent transactions, which an ordinary
/// <c>MAX(number) + 1</c> read does not.
/// </summary>
public interface IInvoiceNumberSequence
{
    Task<long> NextAsync(TenantId tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Guarantees that a caller-supplied idempotency key runs its operation at most once per
/// tenant. Payment submission and usage ingest are both retried by well-behaved clients.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Claims <paramref name="key"/>. Returns true when the caller now owns it and should
    /// do the work; false when it was already claimed, with the stored response if any.
    /// </summary>
    Task<IdempotencyClaim> TryClaimAsync(
        TenantId tenantId,
        string key,
        string requestFingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>Records the outcome so a replay of the same key returns the same answer.</summary>
    Task CompleteAsync(
        TenantId tenantId,
        string key,
        string responsePayload,
        CancellationToken cancellationToken = default);
}

/// <param name="Acquired">True when this caller won the claim and must do the work.</param>
/// <param name="ExistingResponse">The stored response when the key was already completed.</param>
/// <param name="Conflict">True when the key was reused with a different request body.</param>
public readonly record struct IdempotencyClaim(bool Acquired, string? ExistingResponse, bool Conflict);

/// <summary>
/// Lists the tenants that have data to process.
/// <para>
/// Background work cannot use the ambient tenant, because there is no request to take it
/// from. The billing cycle instead iterates tenants and opens a scope per tenant, which
/// keeps the query filters doing their job inside the workers as well.
/// </para>
/// </summary>
public interface ITenantDirectory
{
    Task<IReadOnlyList<TenantId>> ListActiveTenantsAsync(CancellationToken cancellationToken = default);
}
