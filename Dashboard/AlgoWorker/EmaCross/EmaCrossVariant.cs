using AlgoData.Models;
using AlgoWorker.EmaCross.Core;
using AlgoWorker.Configuration;

namespace AlgoWorker.EmaCross;

/// <summary>Everything that differs between Strategy A (15-min) and Strategy B (1-hour).</summary>
public sealed class EmaCrossVariant
{
    public required string Name { get; init; }               // "A" | "B"
    public required string Label { get; init; }              // shown in Telegram messages
    public required StrategyType Strategy { get; init; }     // value stored in Positions.Strategy / SignalLog.Strategy
    public required int CandleMinutes { get; init; }
    public required StrategyConfig Engine { get; init; }
    public required Func<IEntryFilter> CreateFilter { get; init; }
    public required int AdxPeriod { get; init; }

    /// <summary>Strategy A counts trading days, B counts calendar days (section 8.1).</summary>
    public required Func<ExpiryRule> ExpiryRule { get; init; }
    public required Func<double> PremiumStopPercent { get; init; }

    public static EmaCrossVariant A(EmaCrossOptions o) => new()
    {
        Name = "A",
        Label = "EMA 9/21 · 15m",
        Strategy = StrategyType.EmaCrossA,
        CandleMinutes = 15,
        Engine = new StrategyConfig
        {
            FastPeriod = o.FastPeriod,
            SlowPeriod = o.SlowPeriod,
            StopPoints = o.StrategyAStopPoints
        },
        CreateFilter = () => o.StrategyAUseCprFilter ? new CprFilter() : new NoFilter(),
        AdxPeriod = 14,
        ExpiryRule = () => new ExpiryRule(o.StrategyAMinTradingDaysToExpiry, TradingDays: true),
        PremiumStopPercent = () => o.StrategyAPremiumStopPercent
    };

    public static EmaCrossVariant B(EmaCrossOptions o) => new()
    {
        Name = "B",
        Label = "EMA 9/21 · 1h",
        Strategy = StrategyType.EmaCrossB,
        CandleMinutes = 60,
        Engine = new StrategyConfig
        {
            FastPeriod = o.FastPeriod,
            SlowPeriod = o.SlowPeriod,
            StopPoints = o.StrategyBStopPoints,
            BreakevenAtR = o.StrategyBBreakevenAtR > 0 ? o.StrategyBBreakevenAtR : null,
            TrailStartR = o.StrategyBTrailStartR > 0 ? o.StrategyBTrailStartR : null,
            TrailDistanceR = o.StrategyBTrailDistanceR
        },
        CreateFilter = () => o.StrategyBUseAdxRisingFilter ? new AdxRisingFilter() : new NoFilter(),
        AdxPeriod = o.StrategyBAdxPeriod,
        ExpiryRule = () => new ExpiryRule(o.StrategyBMinCalendarDaysToExpiry, TradingDays: false),
        PremiumStopPercent = () => o.StrategyBPremiumStopPercent
    };
}

/// <summary>Minimum time left to expiry at entry: <paramref name="MinDays"/> trading days (weekdays) or calendar days.</summary>
public sealed record ExpiryRule(int MinDays, bool TradingDays)
{
    public bool Allows(DateOnly today, DateOnly expiry)
    {
        if (expiry <= today) return false;
        return TradingDays ? WeekdaysAfter(today, expiry) >= MinDays : expiry.DayNumber - today.DayNumber >= MinDays;
    }

    /// <summary>Weekdays strictly after <paramref name="from"/> up to and including <paramref name="to"/>. NSE holidays are not known here.</summary>
    public static int WeekdaysAfter(DateOnly from, DateOnly to)
    {
        int n = 0;
        for (var d = from.AddDays(1); d <= to; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) n++;
        return n;
    }
}
