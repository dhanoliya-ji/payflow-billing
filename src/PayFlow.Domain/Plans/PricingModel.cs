namespace PayFlow.Domain.Plans;

/// <summary>
/// How a plan turns a subscription into invoice lines.
/// </summary>
public enum PricingModel
{
    /// <summary>One recurring charge per period, regardless of seats or usage.</summary>
    FlatRate,

    /// <summary>The recurring charge is multiplied by the subscription's seat count.</summary>
    PerSeat,

    /// <summary>
    /// A (possibly zero) recurring base charge plus metered usage priced by the plan's
    /// <see cref="Plan.MeteredRates"/>.
    /// </summary>
    Metered,
}
