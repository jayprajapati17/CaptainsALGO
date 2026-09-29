namespace AlgoWorker.Models;

/// <summary>
/// Virtual (paper) position for the 3-Minute MACD strategy. One leg only
/// (current-week ATM), with the doc's dynamic step-trailing stop-loss:
/// SL = Entry - InitialStopLossPoints + StepSize * floor((Peak - Entry) / StepTrigger).
/// This single formula reproduces the doc's worked example exactly (Entry 100 /
/// SL 90 -> Peak 110 -> SL 95 -> Peak 120 -> SL 100 (cost-to-cost) -> Peak 130
/// (target) -> SL 105 -> ... continues in 5-point blocks past the target) and
/// naturally only ever ratchets upward as Peak increases.
/// </summary>
public sealed class MacdPosition
{
    // >>> NEW (Task 3): DB-assigned Id from IPositionRepository.SaveOpenedAsync.
    // -1 if that save failed (non-fatal -- see MacdPositionTracker.OnMacdConfirmedAsync).
    public int Id { get; set; }

    /// <summary>Throttle for per-tick live SignalR broadcasts (Task 8) -- at most ~1/sec per position.</summary>
    public DateTimeOffset LastBroadcastAt { get; set; }

    public required MacdSignal Signal { get; init; }
    public required OptionInstrument Instrument { get; init; }
    public required int LotSize { get; init; }
    public required decimal EntryPremium { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }

    public decimal LastKnownPremium { get; set; }
    public DateTimeOffset LastUpdateSentAt { get; set; }
    public bool IsOpen { get; set; } = true;

    /// <summary>Highest premium observed since entry -- the anchor the step-trailing stop is measured from.</summary>
    public decimal PeakPremium { get; set; }

    /// <summary>Currently active stop-loss premium (starts at Entry - InitialStopLossPoints, then ratchets up in steps).</summary>
    public decimal CurrentStopLossPremium { get; set; }

    /// <summary>True once the first target has been reached at least once -- sent as a one-time "target hit" alert (informational, does not auto-exit; trailing continues).</summary>
    public bool TargetAlertSent { get; set; }

    public decimal PnlRupees => (LastKnownPremium - EntryPremium) * LotSize;

    public double PnlPercent => EntryPremium == 0
        ? 0
        : (double)((LastKnownPremium - EntryPremium) / EntryPremium) * 100.0;
}