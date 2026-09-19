using Microsoft.EntityFrameworkCore;
using PayFlow.Domain;

namespace PayFlow.Infrastructure;

public interface ITenantContext { TenantId TenantId { get; } }
public sealed class TenantContext : ITenantContext
{
    public TenantId TenantId { get; set; }
}

public sealed class PayFlowDbContext(DbContextOptions<PayFlowDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Subscription.UsageRecord> UsageRecords => Set<Subscription.UsageRecord>();
    public DbSet<Subscription.Invoice> Invoices => Set<Subscription.Invoice>();
    public DbSet<Subscription.InvoiceLine> InvoiceLines => Set<Subscription.InvoiceLine>();
    public DbSet<Subscription.Payment> Payments => Set<Subscription.Payment>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Customer>().HasKey(x => x.Id);
        model.Entity<Plan>().HasKey(x => x.Id);
        model.Entity<Subscription>().HasKey(x => x.Id);
        foreach (var type in new[] { typeof(Customer), typeof(Plan), typeof(Subscription), typeof(Subscription.UsageRecord), typeof(Subscription.Invoice), typeof(Subscription.InvoiceLine), typeof(Subscription.Payment) })
            model.Model.FindEntityType(type)!.FindProperty(nameof(Customer.TenantId))!.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<TenantId, Guid>(x => x.Value, x => new TenantId(x)));
        model.Entity<Plan>().Property(x => x.Amount).HasPrecision(18, 2);
        model.Entity<Subscription.UsageRecord>().Property(x => x.Quantity).HasPrecision(18, 4);
        model.Entity<Subscription.Invoice>().Property(x => x.Subtotal).HasPrecision(18, 2);
        model.Entity<Subscription.Invoice>().Property(x => x.Total).HasPrecision(18, 2);
        model.Entity<Subscription.InvoiceLine>().Property(x => x.UnitPrice).HasPrecision(18, 2);
        model.Entity<Subscription.InvoiceLine>().Property(x => x.Amount).HasPrecision(18, 2);
        model.Entity<Subscription.Payment>().Property(x => x.Amount).HasPrecision(18, 2);
        model.Entity<Subscription.Invoice>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.InvoiceId);
        model.Entity<Customer>().HasQueryFilter(x => x.TenantId == tenant.TenantId);
        model.Entity<Plan>().HasQueryFilter(x => x.TenantId == tenant.TenantId);
        model.Entity<Subscription>().HasQueryFilter(x => x.TenantId == tenant.TenantId);
        model.Entity<Subscription.UsageRecord>().HasQueryFilter(x => x.TenantId == tenant.TenantId);
        model.Entity<Subscription.Invoice>().HasQueryFilter(x => x.TenantId == tenant.TenantId);
        model.Entity<Subscription.InvoiceLine>().HasQueryFilter(x => x.TenantId == tenant.TenantId);
        model.Entity<Subscription.Payment>().HasQueryFilter(x => x.TenantId == tenant.TenantId);
    }
}

public interface ICache
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);
    Task SetAsync<T>(string key, T value, TimeSpan expiry, CancellationToken cancellationToken = default);
}
