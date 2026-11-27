namespace AlgoWorker.Models;

/// <summary>How a Liquidity Sweep position is being held right now.</summary>
public enum ReversalHoldMode
{
    /// <summary>EMA 9 above EMA 21 (or price above EMA 21): no fixed target, hold with the trailing stop-loss.</summary>
    TrendHold,

    /// <summary>EMA 9 below EMA 21: hold until Nifty reaches (touches / nears) EMA 21; a candle close above EMA 21 turns it into TrendHold.</summary>
    Ema21Target
}

/// <summary>
/// Virtual (paper) position for the Liquidity Sweep (CE) strategy. The initial stop-loss is a NIFTY SPOT level (the previous
/// candle's low). Once the trade is in <see cref="ReversalHoldMode.TrendHold"/> the app's existing step-trailing stop-loss
/// (premium based, see TrailingStop) also protects the profit.
/// </summary>
public sealed class ReversalPosition
{
    /// <summary>DB-assigned Id (-1 if the save failed -- non-fatal).</summary>
    public int Id { get; set; }

    public DateTimeOffset LastBroadcastAt { get; set; }

    public required ReversalSignal Signal { get; init; }
    public required OptionInstrument Instrument { get; init; }
    public required int LotSize { get; init; }
    public required decimal EntryPremium { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }

    public decimal LastKnownPremium { get; set; }
    public DateTimeOffset LastUpdateSentAt { get; set; }
    public bool IsOpen { get; set; } = true;

    /// <summary>Stop-loss in NIFTY SPOT terms: the previous candle's low (less the buffer). Fixed for the life of the trade.</summary>
    public decimal StopLossSpot { get; set; }

    public ReversalHoldMode Mode { get; set; }

    /// <summary>Ema21Target mode only: Nifty has touched / neared EMA 21 and the decision waits for the candle close.</summary>
    public bool AtEma21Zone { get; set; }

    public decimal EmaShort { get; set; }
    public decimal EmaLong { get; set; }

    public decimal PeakPremium { get; set; }

    /// <summary>The trailing stop-loss in PREMIUM terms (0 until the existing trailing rule has started moving it). Never decreases.</summary>
    public decimal TrailingStopPremium { get; set; }

    public bool TrailingActive => TrailingStopPremium > 0m;

    /// <summary>Candle (open time) on which the position was opened: candle-close rules start from the NEXT candle.</summary>
    public DateTimeOffset EntryCandleTime { get; set; }

    public decimal LastSpot { get; set; }

    public decimal PnlRupees => (LastKnownPremium - EntryPremium) * LotSize;

    public double PnlPercent => EntryPremium == 0
        ? 0
        : (double)((LastKnownPremium - EntryPremium) / EntryPremium) * 100.0;
}
