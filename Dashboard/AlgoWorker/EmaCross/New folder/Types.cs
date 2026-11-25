namespace AlgoWorker.EmaCross.Core;

/// <summary>One closed candle. Time is the candle's START time (exchange wall-clock, IST).</summary>
public sealed record Bar(DateTime Time, double Open, double High, double Low, double Close);

public enum Side { Long = 1, Short = -1 }

public enum ExitReason { StopLoss, BreakevenOrTrail, Crossover, EndOfData }

/// <summary>Strategy parameters (section 2.2 / 4 of the implementation guide). R = StopPoints.</summary>
public sealed class StrategyConfig
{
    public int FastPeriod { get; init; } = 9;
    public int SlowPeriod { get; init; } = 21;
    public double StopPoints { get; init; }
    public double? BreakevenAtR { get; init; }
    public double? TrailStartR { get; init; }
    public double TrailDistanceR { get; init; } = 1.0;
    public double CostPoints { get; init; } = 2.0;

    /// <summary>Strategy A: 15-minute candles, 80-point stop, no breakeven / trailing.</summary>
    public static StrategyConfig StrategyA() => new() { StopPoints = 80 };

    /// <summary>Strategy B: 1-hour candles, 150-point stop, breakeven at 1R, trail from 2R at 1R distance.</summary>
    public static StrategyConfig StrategyB() => new() { StopPoints = 150, BreakevenAtR = 1.0, TrailStartR = 2.0, TrailDistanceR = 1.0 };
}

public sealed class Position
{
    public Side Side { get; init; }
    public DateTime EntryTime { get; init; }
    public double Entry { get; init; }
    public double InitialStop { get; init; }
    public double Stop { get; set; }
    public double Best { get; set; }
}

public sealed record Trade(DateTime EntryTime, DateTime ExitTime, Side Side, double Entry, double Exit, double Pnl, ExitReason Reason);

/// <summary>Central Pivot Range levels. Built from the PREVIOUS day's H/L/C (never today's: look-ahead).</summary>
public readonly record struct CprRange(double Pivot, double Bc, double Tc)
{
    public double Top => Math.Max(Tc, Bc);
    public double Bottom => Math.Min(Tc, Bc);

    public static CprRange FromPreviousDay(double h, double l, double c)
    {
        double pivot = (h + l + c) / 3.0;
        double bc = (h + l) / 2.0;
        double tc = 2.0 * pivot - bc;
        return new CprRange(pivot, bc, tc);
    }
}

/// <summary>What the entry filters get to see for the signal candle. Adx / PrevAdx are null (or NaN) when unknown.</summary>
public readonly record struct FilterContext(Bar Candle, double? Adx, double? PrevAdx, CprRange? Cpr);
