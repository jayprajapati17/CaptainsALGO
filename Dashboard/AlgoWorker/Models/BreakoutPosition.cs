namespace AlgoWorker.Models;

/// <summary>
/// Virtual (paper) position for the Previous-Day High/Low Breakout strategy.
/// Kept as its own small type rather than reusing VirtualPosition/OptionLeg,
/// since this strategy is intentionally simpler: one leg only (current-week
/// ATM), and a time-based intraday exit instead of the EMA strategy's
/// ADX-flat/declining exit.
/// </summary>
public sealed class BreakoutPosition
{
    // >>> CHANGED (Task 3): now the DB-assigned Id from IPositionRepository.SaveOpenedAsync.
    // -1 if that save failed (non-fatal -- see BreakoutPositionTracker.OnBreakoutConfirmedAsync).
    public int Id { get; set; }

    /// <summary>Throttle for per-tick live SignalR broadcasts (Task 8) -- at most ~1/sec per position.</summary>
    public DateTimeOffset LastBroadcastAt { get; set; }

    public required BreakoutSignal Signal { get; init; }
    public required OptionInstrument Instrument { get; init; }
    public required int LotSize { get; init; }
    public required decimal EntryPremium { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }

    public decimal LastKnownPremium { get; set; }
    public DateTimeOffset LastUpdateSentAt { get; set; }
    public bool IsOpen { get; set; } = true;

    // >>> NEW: stop-loss / trailing stop-loss state (points = option premium rupees).
    /// <summary>Highest premium observed since entry -- the anchor the trailing stop is measured back from.</summary>
    public decimal PeakPremium { get; set; }

    /// <summary>The currently active stop-loss premium, per the shared 3-phase trailing
    /// rule (see TrailingStopCalculator.cs) -- never moves down.</summary>
    public decimal CurrentStopLossPremium { get; set; }

    /// <summary>True once profit has reached Phase 2 (or beyond) of the shared trailing rule.</summary>
    public bool TrailingActive { get; set; }

    public decimal PnlRupees => (LastKnownPremium - EntryPremium) * LotSize;

    public double PnlPercent => EntryPremium == 0
        ? 0
        : (double)((LastKnownPremium - EntryPremium) / EntryPremium) * 100.0;
}