namespace AlgoWorker.Services;

/// <summary>
/// Single source of "current time" for the whole system, injected everywhere
/// instead of scattered direct DateTime.Now/DateTimeOffset.Now/DateTime.Today
/// calls. Benefits: one place to reason about timezone behaviour, and the
/// whole app can be driven by a fake clock in tests without touching the
/// system clock.
/// </summary>
public interface IClock
{
    DateTimeOffset Now { get; }
    DateOnly Today { get; }
}

/// <summary>Production implementation -- wraps the real system clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
    public DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
}