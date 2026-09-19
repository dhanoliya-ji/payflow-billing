using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PayFlow.Domain.Plans;

namespace PayFlow.Infrastructure.Persistence.Configurations;

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> builder)
    {
        builder.ToTable("plans");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Code).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();

        // Enums are stored as text. An ordinal column silently reinterprets every
        // existing row the day someone inserts a new member in the middle of the enum,
        // and text keeps the table legible to SQL and to the analytics notebook.
        builder.Property(x => x.Interval).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.PricingModel).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.ComplexProperty(x => x.Price, b => b.MapMoney("price"));

        builder.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();

        builder.HasMany(x => x.MeteredRates)
            .WithOne()
            .HasForeignKey(x => x.PlanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(x => x.MeteredRates).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class MeteredRateConfiguration : IEntityTypeConfiguration<MeteredRate>
{
    public void Configure(EntityTypeBuilder<MeteredRate> builder)
    {
        builder.ToTable("metered_rates");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Metric).HasMaxLength(100).IsRequired();
        builder.Property(x => x.IncludedQuantity).HasPrecision(18, 4);

        builder.ComplexProperty(x => x.UnitPrice, b => b.MapMoney("unit_price"));

        builder.HasIndex(x => new { x.PlanId, x.Metric }).IsUnique();
    }
}
