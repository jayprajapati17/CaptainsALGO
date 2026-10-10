namespace AlgoWorker.Models;

/// <summary>Virtual (paper) position for the Upside Reversal strategy: exits are driven by NIFTY SPOT levels (SL / T1 / T2).</summary>
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

    /// <summary>Current stop-loss in NIFTY SPOT terms. Starts below the setup low; moves to the entry spot ("cost") once T1 is hit.</summary>
    public decimal StopLossSpot { get; set; }

    public bool Target1Hit { get; set; }

    public decimal LastSpot { get; set; }

    public decimal PnlRupees => (LastKnownPremium - EntryPremium) * LotSize;

    public double PnlPercent => EntryPremium == 0
        ? 0
        : (double)((LastKnownPremium - EntryPremium) / EntryPremium) * 100.0;
}
