namespace AlgoWorker.Models;

public enum ReversalSetup
{
    /// <summary>Setup A: two lows within tolerance + bullish rejection candle high broken on a close.</summary>
    DoubleBottom,

    /// <summary>Setup C: green candle sweeps the previous red candle's low and closes back above; confirmed within a few candles.</summary>
    LiquiditySweep
}

/// <summary>
/// A confirmed bullish (long / buy-CE) reversal entry on the 5-min Nifty chart. All levels are NIFTY SPOT levels
/// (the option is only the instrument used to take the trade).
/// </summary>
public sealed record ReversalSignal(
    ReversalSetup Setup,
    DateTimeOffset ConfirmedAtCandleTime,
    decimal EntrySpot,
    decimal StopLossSpot,
    decimal Target1Spot,
    decimal Target2Spot,
    decimal SupportLevel)
{
    public decimal RiskPoints => EntrySpot - StopLossSpot;
    public decimal RewardToT2Points => Target2Spot - EntrySpot;

    public string SetupName => Setup == ReversalSetup.DoubleBottom ? "DOUBLE BOTTOM" : "LIQUIDITY SWEEP";
}
