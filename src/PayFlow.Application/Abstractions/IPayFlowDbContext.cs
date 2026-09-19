using Microsoft.EntityFrameworkCore;
using PayFlow.Domain.Billing;
using PayFlow.Domain.Customers;
using PayFlow.Domain.Invoicing;
using PayFlow.Domain.Payments;
using PayFlow.Domain.Plans;
using PayFlow.Domain.Subscriptions;
using PayFlow.Domain.Usage;

namespace PayFlow.Application.Abstractions;

/// <summary>
/// The persistence surface the use cases are written against.
/// <para>
/// Deliberately an EF Core abstraction rather than a set of repositories. Billing reads
/// are query-shaped - "open invoices due before X, with their lines" - and a repository
/// layer over them either returns <see cref="IQueryable{T}"/> anyway or multiplies into
/// dozens of single-use methods. What this interface does buy is that use cases live in
/// the application project and can be tested against any EF provider.
/// </para>
/// </summary>
public interface IPayFlowDbContext
{
    DbSet<Customer> Customers { get; }

    DbSet<Plan> Plans { get; }

    DbSet<MeteredRate> MeteredRates { get; }

    DbSet<Subscription> Subscriptions { get; }

    DbSet<SubscriptionAdjustment> SubscriptionAdjustments { get; }

    DbSet<UsageRecord> UsageRecords { get; }

    DbSet<Invoice> Invoices { get; }

    DbSet<InvoiceLine> InvoiceLines { get; }

    DbSet<Payment> Payments { get; }

    /// <summary>
    /// Persists pending changes and drains domain events into the outbox in the same
    /// transaction, so a state change and its notification cannot diverge.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="action"/> inside a database transaction, or directly when
    /// the provider does not support them (the in-memory provider used by some tests).
    /// </summary>
    Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);
}
