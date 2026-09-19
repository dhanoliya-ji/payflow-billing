using System.Globalization;

namespace PayFlow.Domain.Common;

/// <summary>
/// An amount in a single ISO-4217 currency.
/// <para>
/// Modelled as a readonly record struct because EF Core maps those as complex types -
/// two real columns, no shadow key, no extra table - while C# still gives us value
/// equality and operators.
/// </para>
/// <para>
/// Amounts carry four decimal places, not two. Unit prices legitimately need the extra
/// precision: a metered plan priced at $0.002 per API call is a real thing to sell, and
/// storing money at two decimals rounds that rate to zero and bills the usage for
/// nothing. Two decimals is a property of what a customer can be <em>charged</em>, not of
/// every amount in the system, so rounding to minor units happens at the point that
/// matters - <see cref="Round"/>, applied to invoice line amounts, invoice totals and
/// payment amounts.
/// </para>
/// <para>
/// Rounding is <see cref="MidpointRounding.ToEven"/> (banker's rounding). Away-from-zero
/// rounding biases a long run of half-cent amounts consistently upward.
/// </para>
/// <para>
/// Arithmetic between different currencies throws rather than silently coercing: a
/// billing system that adds USD to EUR produces invoices nobody can reconcile.
/// </para>
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public const string DefaultCurrency = "USD";

    /// <summary>Decimal places an amount is stored with. Matches the numeric(18,4) columns.</summary>
    public const int StorageScale = 4;

    /// <summary>Decimal places a customer-facing amount is rounded to.</summary>
    public const int MinorUnitScale = 2;

    public Money(decimal amount, string currency = DefaultCurrency)
    {
        Currency = NormalizeCurrency(currency);
        Amount = decimal.Round(amount, StorageScale, MidpointRounding.ToEven);
    }

    public decimal Amount { get; init; }

    public string Currency { get; init; }

    public bool IsZero => Amount == 0m;

    public bool IsPositive => Amount > 0m;

    public bool IsNegative => Amount < 0m;

    public static Money Zero(string currency = DefaultCurrency) => new(0m, currency);

    /// <summary>
    /// Rounds to the currency's minor units - the amount that can actually be charged.
    /// Applied when a computed value becomes a line amount, an invoice total or a payment.
    /// </summary>
    public Money Round() => this with { Amount = decimal.Round(Amount, MinorUnitScale, MidpointRounding.ToEven) };

    /// <summary>Multiplies by a quantity — the per-line "unit price times quantity".</summary>
    public Money Times(decimal quantity) => new(Amount * quantity, Currency);

    /// <summary>
    /// Scales by a 0..1 fraction. Used by proration, where a mid-cycle plan change is
    /// charged for the unused fraction of the billing period.
    /// </summary>
    public Money Prorate(decimal fraction)
    {
        if (fraction is < 0m or > 1m)
        {
            throw new DomainValidationException(nameof(fraction), "Proration fraction must be between 0 and 1.");
        }

        return new Money(Amount * fraction, Currency);
    }

    public Money Negate() => new(-Amount, Currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static Money operator *(Money value, decimal multiplier) => value.Times(multiplier);

    public static bool operator >(Money left, Money right) => Compare(left, right) > 0;

    public static bool operator <(Money left, Money right) => Compare(left, right) < 0;

    public static bool operator >=(Money left, Money right) => Compare(left, right) >= 0;

    public static bool operator <=(Money left, Money right) => Compare(left, right) <= 0;

    public static Money Add(Money left, Money right) => left + right;

    public static Money Subtract(Money left, Money right) => left - right;

    public static Money Multiply(Money value, decimal multiplier) => value.Times(multiplier);

    /// <summary>Sums a sequence, using <paramref name="fallbackCurrency"/> when it is empty.</summary>
    public static Money Sum(IEnumerable<Money> values, string fallbackCurrency = DefaultCurrency)
    {
        ArgumentNullException.ThrowIfNull(values);

        Money? total = null;
        foreach (var value in values)
        {
            total = total is null ? value : total.Value + value;
        }

        return total ?? Zero(fallbackCurrency);
    }

    public int CompareTo(Money other) => Compare(this, other);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Amount:0.00##} {Currency}");

    private static int Compare(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount.CompareTo(right.Amount);
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (!string.Equals(left.Currency, right.Currency, StringComparison.Ordinal))
        {
            throw new DomainException($"Cannot combine {left.Currency} and {right.Currency} amounts.");
        }
    }

    private static string NormalizeCurrency(string currency)
    {
        var normalized = Guard.NotNullOrWhiteSpace(currency).ToUpperInvariant();
        if (normalized.Length != 3 || !normalized.All(char.IsAsciiLetterUpper))
        {
            throw new DomainValidationException(nameof(currency), "Currency must be a three-letter ISO 4217 code.");
        }

        return normalized;
    }
}
