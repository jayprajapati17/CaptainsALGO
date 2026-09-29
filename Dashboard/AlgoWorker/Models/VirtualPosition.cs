namespace AlgoWorker.Models;

/// <summary>
/// A paper-traded position opened the moment a signal is confirmed. NO real order
/// is ever placed against Upstox for this -- it exists purely so the bot can push
/// live P&amp;L updates to Telegram, per the user's explicit choice (design doc Section 6, point 4).
/// </summary>
public sealed class VirtualPosition
{
    // >>> CHANGED (Task 3): now the DB-assigned Id from IPositionRepository.SaveOpenedAsync,
    // set right after the position is created. -1 if that save failed (non-fatal --
    // see VirtualPositionTracker.OpenLegAsync). Defaults to 0 in the brief window
    // between construction and the save completing.
    public int Id { get; set; }

    /// <summary>Throttle for per-tick live SignalR broadcasts (Task 8) -- at most ~1/sec per position.</summary>
    public DateTimeOffset LastBroadcastAt { get; set; }

    public required TradeSignal Signal { get; init; }
    public required OptionInstrument Instrument { get; init; }
    public required int LotSize { get; init; }
    public required decimal EntryPremium { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }

    // >>> NEW: which leg this position represents (current-week ITM or
    // next-week ATM) -- every signal now opens both simultaneously.
    public required OptionLeg Leg { get; init; }

    public decimal LastKnownPremium { get; set; }
    public double AdxPeakSinceEntry { get; set; }
    public DateTimeOffset LastUpdateSentAt { get; set; }
    public bool IsOpen { get; set; } = true;

    // >>> NEW: per-leg stop-loss / trailing stop-loss state (points = option premium rupees).
    /// <summary>Highest premium observed since this leg's entry -- the anchor the trailing stop is measured back from.</summary>
    public decimal PeakPremium { get; set; }

    /// <summary>Currently active stop-loss premium for this leg. Fixed (Entry - EmaStopLossPoints) until trailing
    /// activates, then ratchets up to (PeakPremium - EmaTrailingStepPoints) -- never moves down.</summary>
    public decimal CurrentStopLossPremium { get; set; }

    /// <summary>True once this leg's profit has crossed EmaTrailingTriggerPoints and its stop is trailing the peak.</summary>
    public bool TrailingActive { get; set; }

    public decimal PnlRupees => (LastKnownPremium - EntryPremium) * LotSize;

    public double PnlPercent => EntryPremium == 0
        ? 0
        : (double)((LastKnownPremium - EntryPremium) / EntryPremium) * 100.0;
}