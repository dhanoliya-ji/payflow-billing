using PayFlow.Application.Abstractions;

namespace PayFlow.Infrastructure.Time;

/// <summary>The real clock, always in UTC. Every stored instant is UTC; display-time
/// conversion is the caller's problem, not the billing engine's.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
