namespace AlgoWorker.Models;

/// <summary>
/// A confirmed LIQUIDITY SWEEP buy-CE entry on the 5-min Nifty chart. All levels are NIFTY SPOT levels
/// (the option is only the instrument used to take the trade).
/// Sweep: the previous candle is red, the current candle trades below its low and closes back ABOVE that low;
/// then a confirmation candle closes above the sweep candle's high. The stop-loss is the previous (red) candle's low.
/// </summary>
public sealed record ReversalSignal(
    DateTimeOffset ConfirmedAtCandleTime,
    decimal EntrySpot,
    decimal StopLossSpot,
    decimal SweepLowSpot,
    decimal EmaShort,
    decimal EmaLong)
{
    public decimal RiskPoints => EntrySpot - StopLossSpot;

    /// <summary>
    /// True: hold for a long move with the trailing stop-loss (EMA 9 is above EMA 21, or price is already above EMA 21).
    /// False: hold only until Nifty reaches EMA 21; if price crosses above it, switch to the long hold.
    /// </summary>
    public bool HoldWithTrailing => EmaShort >= EmaLong || EntrySpot >= EmaLong;

    public string SetupName => "LIQUIDITY SWEEP";
}
