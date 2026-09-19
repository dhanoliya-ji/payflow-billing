using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PayFlow.Domain.Customers;

namespace PayFlow.Infrastructure.Persistence.Configurations;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(x => x.ExternalReference).HasMaxLength(200);
        builder.Property(x => x.PaymentMethodToken).HasMaxLength(200);

        // One customer per email per tenant. Scoped to the tenant, not global: two
        // tenants selling to the same person is normal and must not collide.
        builder.HasIndex(x => new { x.TenantId, x.Email }).IsUnique();

        builder.HasIndex(x => new { x.TenantId, x.ExternalReference });
    }
}
