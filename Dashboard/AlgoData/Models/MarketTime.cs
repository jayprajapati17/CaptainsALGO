namespace AlgoData.Models;

/// <summary>
/// Everything is stored and computed in UTC (DateTimeOffset.UtcNow). The Indian exchange runs on IST, which has no
/// daylight saving (fixed +05:30), so "market clock" questions -- what time is it on the exchange, which trading
/// date is it -- are answered here, in one place, by converting a UTC instant. Time settings such as
/// SessionStartTime "09:15" are IST wall-clock values and are compared against <see cref="ToIst"/> of the instant.
/// </summary>
public static class MarketTime
{
    public static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    /// <summary>The same instant expressed in IST (for time-of-day / weekday / date comparisons and for display).</summary>
    public static DateTimeOffset ToIst(this DateTimeOffset utc) => utc.ToOffset(IstOffset);

    /// <summary>The same instant for a nullable timestamp.</summary>
    public static DateTimeOffset? ToIst(this DateTimeOffset? utc) => utc?.ToOffset(IstOffset);

    /// <summary>The exchange (IST) calendar date right now.</summary>
    public static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToIst().DateTime);

    /// <summary>Midnight (start of the IST trading day) of the given date, as a UTC-comparable instant.</summary>
    public static DateTimeOffset StartOfDay(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), IstOffset);
}
