namespace AlgoWorker.Models;

public enum SignalDirection
{
    /// <summary>EMA21 crossed above EMA50 -- bullish, buy CE.</summary>
    GoldenCross,

    /// <summary>EMA21 crossed below EMA50 -- bearish, buy PE.</summary>
    DeathCross
}

public enum ConfidenceLevel
{
    /// <summary>Golden Cross + ADX rising. Backtest: 56.7% win, avg +0.234%/2-day (n=141, p=0.093).</summary>
    High,

    /// <summary>Golden Cross without ADX rising confirmed.</summary>
    Medium,

    /// <summary>Death Cross (any filter). Backtest found NO statistically proven edge -- informational only.</summary>
    Low
}

/// <summary>A confirmed (post 1-hour-hold) EMA21/50 crossover signal, ready to alert on.</summary>
public sealed record TradeSignal(
    SignalDirection Direction,
    ConfidenceLevel Confidence,
    DateTimeOffset ConfirmedAtCandleTime,
    decimal NiftySpotAtConfirmation,
    double AdxAtConfirmation,
    double AdxNCandlesAgo,
    // >>> NEW: true when this signal was reconstructed by looking backward
    // through history right after startup/seeding (see
    // SignalEngine.EvaluateCatchUpSignal), rather than detected live,
    // candle-by-candle. Lets the Telegram message make clear this crossover
    // may have originally happened a while ago and is just still active now.
    bool IsCatchUp = false);