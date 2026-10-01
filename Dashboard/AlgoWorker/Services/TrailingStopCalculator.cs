using AlgoWorker.Configuration;
using AlgoWorker.Configuration;

namespace AlgoWorker.Services;

/// <summary>
/// The "Final Updated Trailing Rule" -- one shared 3-phase stop-loss rule used
/// identically by all three strategies (EMA, Breakout, MACD), replacing each
/// strategy's previous separate SL/trailing config.
///
/// Phases (all thresholds are "points gained" = PeakPremium - EntryPremium):
///   Phase 1 (gained &lt; Phase2TriggerPoints):                    SL = Entry - InitialRiskPoints
///   Phase 2 (Phase2TriggerPoints &lt;= gained &lt; Phase3TriggerPoints): SL = Entry - Phase2RiskPoints  (flat plateau)
///   Phase 3 (gained &gt;= Phase3TriggerPoints):                     SL = Peak - ContinuousGapPoints    (ratchets up only)
///
/// Worked example with the default config (Entry = 100):
///   Peak 100 (entry) -> SL 85   (Phase 1: 100 - 15)
///   Peak 115 (+15)   -> SL 95   (Phase 2 starts: 100 - 5)
///   Peak 125 (+25)   -> SL 100  (Phase 3 starts: 125 - 25 -- cost-to-cost)
///   Peak 130 (+30)   -> SL 105  (Phase 3: 130 - 25)
///   Peak 135 (+35)   -> SL 110  (Phase 3: 135 - 25)
///
/// The result is monotonically non-decreasing as Peak rises (Phase 1 SL &lt;
/// Phase 2 SL by construction, and Phase 3 SL only grows with Peak), so
/// callers can simply recompute and assign every tick -- no separate
/// "only if greater" ratchet guard is needed.
/// </summary>
public static class TrailingStopCalculator
{
    public static decimal ComputeStopLoss(decimal entryPremium, decimal peakPremium, StrategyOptions options)
    {
        var gained = peakPremium - entryPremium;

        if (gained >= (decimal)options.TrailingPhase3TriggerPoints)
            return peakPremium - (decimal)options.TrailingContinuousGapPoints;

        if (gained >= (decimal)options.TrailingPhase2TriggerPoints)
            return entryPremium - (decimal)options.TrailingPhase2RiskPoints;

        return entryPremium - (decimal)options.TrailingInitialRiskPoints;
    }
}