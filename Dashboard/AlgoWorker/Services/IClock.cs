namespace AlgoWorker.Services;

/// <summary>
/// Single source of "current time" for the whole system, injected everywhere
/// instead of scattered direct DateTime.UtcNow/DateTimeOffset.UtcNow/DateTime.Today
/// calls. Benefits: one place to reason about timezone behaviour, and the
/// whole app can be driven by a fake clock in tests without touching the
/// system clock.
/// </summary>
public interface IClock
{
    /// <summary>Current instant in UTC.</summary>
    DateTimeOffset UtcNow { get; }
    /// <summary>Current exchange (IST) calendar date.</summary>
    DateOnly Today { get; }
}

/// <summary>Production implementation -- wraps the real system clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public DateOnly Today => AlgoData.Models.MarketTime.Today;
}