using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Common;
using PayFlow.Infrastructure.Persistence;

namespace PayFlow.Infrastructure.Idempotency;

/// <summary>
/// Idempotency backed by a unique index.
/// <para>
/// The claim is won by inserting a row and letting the database reject the loser. A
/// check-then-insert in application code leaves a window where two concurrent retries
/// both see "not claimed" and both do the work - which, for a payment, means charging
/// the card twice.
/// </para>
/// </summary>
public sealed class EfIdempotencyStore(PayFlowDbContext db, IClock clock) : IIdempotencyStore
{
    public async Task<IdempotencyClaim> TryClaimAsync(
        TenantId tenantId,
        string key,
        string requestFingerprint,
        CancellationToken cancellationToken = default)
    {
        var existing = await db.IdempotencyRecords
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Key == key, cancellationToken);

        if (existing is not null)
        {
            return Evaluate(existing, requestFingerprint);
        }

        db.IdempotencyRecords.Add(new IdempotencyRecord(tenantId, key, requestFingerprint, clock.UtcNow));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new IdempotencyClaim(Acquired: true, ExistingResponse: null, Conflict: false);
        }
        catch (DbUpdateException)
        {
            // Another request claimed the key between the read and the insert. Detach the
            // failed entity so the context is usable, then read the winner's row.
            foreach (var entry in db.ChangeTracker.Entries<IdempotencyRecord>().ToList())
            {
                entry.State = EntityState.Detached;
            }

            var winner = await db.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Key == key, cancellationToken);

            return winner is null
                ? new IdempotencyClaim(Acquired: false, ExistingResponse: null, Conflict: false)
                : Evaluate(winner, requestFingerprint);
        }
    }

    public async Task CompleteAsync(
        TenantId tenantId,
        string key,
        string responsePayload,
        CancellationToken cancellationToken = default)
    {
        var record = await db.IdempotencyRecords
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Key == key, cancellationToken);

        if (record is null)
        {
            return;
        }

        record.Complete(responsePayload, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static IdempotencyClaim Evaluate(IdempotencyRecord record, string requestFingerprint)
    {
        if (!string.Equals(record.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            return new IdempotencyClaim(Acquired: false, ExistingResponse: null, Conflict: true);
        }

        return new IdempotencyClaim(Acquired: false, ExistingResponse: record.ResponsePayload, Conflict: false);
    }
}
