namespace AlgoWorker.Models;

public enum MacdDirection
{
    /// <summary>Fast MACD crossed above Signal while both lines are at/above the zero line -- buy CE.</summary>
    Bullish,

    /// <summary>Fast MACD crossed below Signal while both lines are at/below the zero line -- buy PE.</summary>
    Bearish
}

/// <summary>
/// A confirmed 3-min MACD(12,26,9) crossover, zero-line filtered. No multi-candle
/// hold/confirmation like the EMA strategy -- per the doc, entry is on the close
/// of the trigger candle itself.
/// </summary>
public sealed record MacdSignal(
    MacdDirection Direction,
    DateTimeOffset ConfirmedAtCandleTime,
    decimal NiftySpotAtConfirmation,
    double MacdLine,
    double SignalLine);
