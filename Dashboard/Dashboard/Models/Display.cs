using System.Globalization;
using AlgoData.Models;

namespace Dashboard.Models;

/// <summary>Small formatting helpers shared by the History and Signals Log views.</summary>
public static class Display
{
    private static readonly CultureInfo In = CultureInfo.GetCultureInfo("en-IN");

    public static string Strategy(StrategyType s) => s switch
    {
        StrategyType.Ema => "EMA",
        StrategyType.Breakout => "Breakout",
        StrategyType.Macd => "MACD",
        StrategyType.Reversal => "Reversal",
        _ => s.ToString()
    };

    public static string DirectionLabel(string direction) => direction switch
    {
        "GoldenCross" => "Golden Cross",
        "DeathCross" => "Death Cross",
        _ => direction // Up / Down / Bullish / Bearish read fine as-is
    };

    public static bool IsBullish(string direction) => direction is "GoldenCross" or "Up" or "Bullish";

    public static string Leg(PositionLeg? leg) => leg switch
    {
        PositionLeg.CurrentWeekItm => "Curr-wk ITM",
        PositionLeg.NextWeekAtm => "Next-wk ATM",
        _ => string.Empty
    };

    public static string Number(decimal v) => v.ToString("N2", In);

    public static string Money(decimal? v)
    {
        if (v is null) return "\u2014";
        var sign = v > 0 ? "+" : v < 0 ? "-" : "";
        return $"{sign}\u20B9{Math.Abs(v.Value).ToString("N2", In)}";
    }

    public static string Pct(double? v)
    {
        if (v is null) return "";
        var sign = v > 0 ? "+" : v < 0 ? "-" : "";
        return $"{sign}{Math.Abs(v.Value):0.0}%";
    }

    public static string PnlClass(decimal? v) => v switch
    {
        > 0 => "pnl-positive",
        < 0 => "pnl-negative",
        _ => "text-secondary"
    };

    public static string Duration(TimeSpan? t)
    {
        if (t is null) return "\u2014";
        var d = t.Value;
        if (d.TotalMinutes < 1) return "<1m";
        return d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m" : $"{d.Minutes}m";
    }

    public static string ConfidenceBadge(ConfidenceLevel? c) => c switch
    {
        ConfidenceLevel.High => "text-bg-success",
        ConfidenceLevel.Medium => "text-bg-warning",
        ConfidenceLevel.Low => "text-bg-secondary",
        _ => "text-bg-dark"
    };
}