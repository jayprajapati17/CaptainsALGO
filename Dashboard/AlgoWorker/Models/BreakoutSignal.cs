namespace AlgoWorker.Models;

public enum BreakoutDirection
{
    /// <summary>5-min candle closed ABOVE previous day's high -- buy CE.</summary>
    Up,

    /// <summary>5-min candle closed BELOW previous day's low -- buy PE.</summary>
    Down
}

/// <summary>A confirmed previous-day high/low breakout on the 5-min timeframe.</summary>
public sealed record BreakoutSignal(
    BreakoutDirection Direction,
    DateTimeOffset ConfirmedAtCandleTime,
    decimal NiftySpotAtConfirmation,
    decimal BrokenLevel);
