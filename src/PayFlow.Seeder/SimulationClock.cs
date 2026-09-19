using PayFlow.Application.Abstractions;

namespace PayFlow.Seeder;

/// <summary>
/// A clock the simulation drives.
/// <para>
/// This is the whole reason <see cref="IClock"/> exists as a port. Generating a year of
/// billing history against the real clock would take a year; against a movable one it
/// takes seconds, and every invoice, retry and write-off is produced by the same
/// <c>BillingCycleService</c> that runs in production rather than by fixture data that
/// only looks plausible.
/// </para>
/// </summary>
public sealed class SimulationClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = start;

    public void AdvanceTo(DateTimeOffset instant)
    {
        if (instant < UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(instant), "The simulation clock only moves forward.");
        }

        UtcNow = instant;
    }

    public void AdvanceDays(int days) => UtcNow = UtcNow.AddDays(days);

    /// <summary>
    /// Rewinds to the start of the window. Used between tenants so each replays the same
    /// months and both finish at the present, rather than the second tenant's history
    /// starting where the first one's ended.
    /// </summary>
    public void Reset(DateTimeOffset to) => UtcNow = to;
}
