namespace AlgoWorker.Models;

public enum BreakoutDirection
{
    /// <summary>5-min candle closed ABOVE the opening-range high -- buy CE.</summary>
    Up,

    /// <summary>5-min candle closed BELOW the opening-range low -- buy PE.</summary>
    Down
}

/// <summary>A confirmed Opening Range Breakout (ORB) on the 5-min timeframe.</summary>
public sealed record BreakoutSignal(
    BreakoutDirection Direction,
    DateTimeOffset ConfirmedAtCandleTime,
    decimal NiftySpotAtConfirmation,
    decimal BrokenLevel);