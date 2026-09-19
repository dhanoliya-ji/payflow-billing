using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Billing;
using PayFlow.Domain.Common;
using PayFlow.Domain.Customers;
using PayFlow.Domain.Events;
using PayFlow.Domain.Invoicing;
using PayFlow.Domain.Payments;
using PayFlow.Domain.Plans;
using PayFlow.Domain.Subscriptions;
using PayFlow.Domain.Usage;
using PayFlow.Infrastructure.Idempotency;
using PayFlow.Infrastructure.Outbox;

namespace PayFlow.Infrastructure.Persistence;

/// <summary>Stores a <see cref="TenantId"/> as the plain uuid it wraps, so the value
/// object costs nothing at the schema level and stays queryable from SQL.</summary>
internal sealed class TenantIdConverter()
    : ValueConverter<TenantId, Guid>(id => id.Value, value => new TenantId(value));

public sealed class PayFlowDbContext(DbContextOptions<PayFlowDbContext> options, ITenantContext tenant)
    : DbContext(options), IPayFlowDbContext
{
    private static readonly JsonSerializerOptions EventSerializerOptions = new(JsonSerializerDefaults.Web);

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Plan> Plans => Set<Plan>();

    public DbSet<MeteredRate> MeteredRates => Set<MeteredRate>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<SubscriptionAdjustment> SubscriptionAdjustments => Set<SubscriptionAdjustment>();

    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<InvoiceSequence> InvoiceSequences => Set<InvoiceSequence>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        ArgumentNullException.ThrowIfNull(model);

        model.ApplyConfigurationsFromAssembly(typeof(PayFlowDbContext).Assembly);

        // Tenant isolation is a model-level filter rather than a convention every query
        // has to remember. A developer who writes `db.Invoices.ToListAsync()` gets their
        // tenant's invoices; there is no spelling of that query that quietly returns
        // everyone's. IgnoreQueryFilters() is the deliberate, greppable escape hatch,
        // used only by the migration bootstrap and the outbox dispatcher.
        model.Entity<Customer>().HasQueryFilter(x => x.TenantId == tenant.TenantId);
        model.Entity<Plan>().HasQueryFilter(x => x.TenantId == tenant.TenantId);
        model.Entity<MeteredRate>().HasQueryFilter(x => x.TenantId == tenant.TenantId);
        model.Entity<Subscription>().HasQueryFilter(x => x.TenantId == tenant.TenantId);
        model.Entity<SubscriptionAdjustment>().HasQueryFilter(x => x.TenantId == tenant.TenantId);
        model.Entity<UsageRecord>().HasQueryFilter(x => x.TenantId == tenant.TenantId);
        model.Entity<Invoice>().HasQueryFilter(x => x.TenantId == tenant.TenantId);
        model.Entity<InvoiceLine>().HasQueryFilter(x => x.TenantId == tenant.TenantId);
        model.Entity<Payment>().HasQueryFilter(x => x.TenantId == tenant.TenantId);

        ApplySnakeCaseNames(model);
    }

    /// <summary>
    /// Teaches EF that <see cref="TenantId"/> is a uuid wherever it appears.
    /// <para>
    /// Registered as a convention rather than patched onto the finished model: the
    /// property has to be recognised as mappable while the model is being discovered, or
    /// EF rejects it as an unsupported type before any post-processing runs.
    /// </para>
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        configuration.Properties<TenantId>().HaveConversion<TenantIdConverter>();

        // Money amounts are fixed-point everywhere. Left to the provider default, a
        // decimal silently becomes numeric with unbounded precision on PostgreSQL.
        configuration.Properties<decimal>().HavePrecision(18, 4);
    }

    /// <summary>
    /// Persists changes and moves every domain event raised during this unit of work into
    /// the outbox before the same transaction commits.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampTenantOnNewEntities();
        DrainDomainEventsToOutbox();
        return await base.SaveChangesAsync(cancellationToken);
    }

    Task<int> IPayFlowDbContext.SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<T> InTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Already inside a transaction the caller opened: join it rather than nesting,
        // so an inner failure rolls the whole operation back rather than half of it.
        if (Database.CurrentTransaction is not null)
        {
            return await action(cancellationToken);
        }

        // The in-memory provider used by some unit tests has no transaction support.
        // Running the action directly keeps those tests meaningful instead of forcing
        // every caller to branch on the provider.
        if (!Database.IsRelational())
        {
            return await action(cancellationToken);
        }

        var strategy = Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async ct =>
        {
            await using IDbContextTransaction transaction = await Database.BeginTransactionAsync(ct);
            var result = await action(ct);
            await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }

    /// <summary>
    /// Fills in the tenant on entities added without one. Every aggregate takes its
    /// tenant in its constructor, so this is a safety net rather than the mechanism -
    /// but a row that reaches the database with an empty tenant would be invisible to
    /// every filtered query and effectively lost.
    /// </summary>
    private void StampTenantOnNewEntities()
    {
        if (!tenant.IsResolved)
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Added && entry.Entity.TenantId.IsEmpty)
            {
                entry.Property(nameof(Entity.TenantId)).CurrentValue = tenant.TenantId;
            }
        }
    }

    private void DrainDomainEventsToOutbox()
    {
        var entities = ChangeTracker.Entries<Entity>()
            .Select(x => x.Entity)
            .Where(x => x.DomainEvents.Count > 0)
            .ToList();

        if (entities.Count == 0)
        {
            return;
        }

        foreach (var entity in entities)
        {
            foreach (var domainEvent in entity.DomainEvents)
            {
                OutboxMessages.Add(new OutboxMessage(
                    domainEvent.TenantId,
                    domainEvent.EventType,
                    JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), EventSerializerOptions),
                    domainEvent.OccurredAt));
            }

            entity.ClearDomainEvents();
        }
    }

    /// <summary>
    /// Renames tables and columns to snake_case.
    /// <para>
    /// Not cosmetic: unquoted identifiers in PostgreSQL fold to lower case, so a
    /// PascalCase schema forces every hand-written query - including the analytics
    /// notebook that reads this database directly - to quote every identifier.
    /// </para>
    /// </summary>
    private static void ApplySnakeCaseNames(ModelBuilder model)
    {
        foreach (var entityType in model.Model.GetEntityTypes())
        {
            if (entityType.GetTableName() is { } tableName)
            {
                entityType.SetTableName(ToSnakeCase(tableName));
            }

            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.GetColumnName()));
            }

            foreach (var key in entityType.GetKeys())
            {
                key.SetName(ToSnakeCase(key.GetName() ?? ""));
            }

            foreach (var index in entityType.GetIndexes())
            {
                index.SetDatabaseName(ToSnakeCase(index.GetDatabaseName() ?? ""));
            }

            foreach (var foreignKey in entityType.GetForeignKeys())
            {
                foreignKey.SetConstraintName(ToSnakeCase(foreignKey.GetConstraintName() ?? ""));
            }

            foreach (var complexProperty in entityType.GetComplexProperties())
            {
                foreach (var property in complexProperty.ComplexType.GetProperties())
                {
                    property.SetColumnName(ToSnakeCase(property.GetColumnName()));
                }
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];

            if (char.IsUpper(current))
            {
                // Insert a separator at a lower-to-upper boundary (PeriodStart) and at the
                // end of an acronym run (HTTPStatus -> http_status), but never twice.
                var previous = i > 0 ? name[i - 1] : '\0';
                var next = i + 1 < name.Length ? name[i + 1] : '\0';

                var startsNewWord = i > 0
                    && previous != '_'
                    && (!char.IsUpper(previous) || (char.IsUpper(previous) && char.IsLower(next)));

                if (startsNewWord)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }
}
