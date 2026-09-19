using System.Runtime.CompilerServices;

namespace PayFlow.Domain.Common;

/// <summary>
/// Argument checks that throw <see cref="DomainValidationException"/> rather than the
/// BCL argument exceptions, so the whole domain reports invalid input one way and the
/// API can translate a single exception type.
/// </summary>
internal static class Guard
{
    public static string NotNullOrWhiteSpace(string? value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(name ?? "value", $"{name} is required.");
        }

        return value.Trim();
    }

    public static Guid NotEmpty(Guid value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException(name ?? "value", $"{name} is required.");
        }

        return value;
    }

    public static TenantId Tenant(TenantId value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value.IsEmpty)
        {
            throw new DomainValidationException(name ?? "tenantId", "A tenant is required.");
        }

        return value;
    }

    public static decimal Positive(decimal value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value <= 0m)
        {
            throw new DomainValidationException(name ?? "value", $"{name} must be greater than zero.");
        }

        return value;
    }

    public static decimal NotNegative(decimal value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < 0m)
        {
            throw new DomainValidationException(name ?? "value", $"{name} cannot be negative.");
        }

        return value;
    }

    public static int PositiveCount(int value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value <= 0)
        {
            throw new DomainValidationException(name ?? "value", $"{name} must be at least 1.");
        }

        return value;
    }
}
