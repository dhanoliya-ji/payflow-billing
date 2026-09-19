using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Common;

namespace PayFlow.Infrastructure.Persistence;

/// <summary>
/// Allocates the next invoice number for a tenant.
/// <para>
/// On PostgreSQL this is a single <c>INSERT … ON CONFLICT DO UPDATE … RETURNING</c>,
/// which takes a row lock and increments atomically. The obvious alternative -
/// <c>SELECT MAX(number) + 1</c> - hands the same number to two concurrent billing runs
/// and then fails one of them on the unique index, losing an invoice.
/// </para>
/// <para>
/// The allocation joins the caller's transaction, so a rolled-back invoice does not burn
/// a number and leave a gap in the tenant's sequence.
/// </para>
/// </summary>
public sealed class InvoiceNumberSequence(PayFlowDbContext db) : IInvoiceNumberSequence
{
    public async Task<long> NextAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        if (db.Database.IsNpgsql())
        {
            var next = await db.Database
                .SqlQueryRaw<long>(
                    """
                    INSERT INTO invoice_sequences (tenant_id, last_number)
                    VALUES ({0}, 1)
                    ON CONFLICT (tenant_id)
                    DO UPDATE SET last_number = invoice_sequences.last_number + 1
                    RETURNING last_number AS "Value"
                    """,
                    tenantId.Value)
                .ToListAsync(cancellationToken);

            return next[0];
        }

        // Providers without upsert support (the SQLite database used by the integration
        // tests) fall back to read-modify-write. Correct under the tests' serialised
        // access; not what runs in production.
        var sequence = await db.InvoiceSequences.FirstOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);

        if (sequence is null)
        {
            sequence = new InvoiceSequence(tenantId);
            db.InvoiceSequences.Add(sequence);
        }

        var number = sequence.Next();
        await db.SaveChangesAsync(cancellationToken);
        return number;
    }
}
