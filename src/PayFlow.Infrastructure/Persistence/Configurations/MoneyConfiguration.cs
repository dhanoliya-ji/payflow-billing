using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PayFlow.Domain.Common;

namespace PayFlow.Infrastructure.Persistence.Configurations;

/// <summary>
/// Shared mapping helpers for the value objects that appear on several aggregates.
/// </summary>
internal static class ValueObjectMapping
{
    /// <summary>
    /// Currency is stored per amount rather than once per row. It is three bytes of
    /// redundancy that make every monetary column self-describing in SQL, which matters
    /// because the analytics notebook and any finance query read these columns directly
    /// without the C# types.
    /// </summary>
    public static void MapMoney(this ComplexPropertyBuilder<Money> builder, string prefix)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // numeric(18,4), matching Money.StorageScale: unit prices carry four decimals so a
        // sub-cent metered rate is not rounded away before it is ever multiplied out.
        builder.Property(x => x.Amount)
            .HasColumnName($"{prefix}_amount")
            .HasPrecision(18, Domain.Common.Money.StorageScale);

        builder.Property(x => x.Currency)
            .HasColumnName($"{prefix}_currency")
            .HasMaxLength(3)
            .IsFixedLength();
    }
}
