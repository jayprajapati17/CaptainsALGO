using AlgoWorker.Configuration;

namespace AlgoWorker.Services;

/// <summary>
/// The shared "step-trailing" stop-loss rule, used identically by all three
/// strategies (EMA, Breakout, MACD). All thresholds are "points gained" =
/// PeakPremium - EntryPremium, and every number is configurable.
///
///   Before the target (gained &lt; TrailingTargetPoints):
///       SL = Entry - InitialRiskPoints + StepSlPoints x floor(gained / StepPoints)
///   At/after the target (gained &gt;= TrailingTargetPoints):
///       SL = Entry + TargetLockPoints + StepSlPoints x floor((gained - TargetPoints) / StepPoints)
///
/// Worked example with the defaults (Initial 15, Step 5, StepSl 3, Target 30, Lock 20; Entry = 100):
///   Peak 100 -> SL 85    Peak 105 -> SL 88    Peak 110 -> SL 91
///   Peak 115 -> SL 94    Peak 120 -> SL 97    Peak 125 -> SL 100
///   Peak 130 (target) -> SL 120  (jumps to Entry + 20)
///   Peak 135 -> SL 123   Peak 140 -> SL 126   Peak 145 -> SL 129 ...
///
/// The result never decreases as Peak rises, so callers can simply recompute
/// and assign it on every tick -- no separate "only if greater" guard needed.
/// </summary>
public static class TrailingStopCalculator
{
    public static decimal ComputeStopLoss(decimal entryPremium, decimal peakPremium, StrategyOptions options)
    {
        var gained = Math.Max(0m, peakPremium - entryPremium);
        var step = (decimal)options.TrailingStepPoints;
        var stepSl = (decimal)options.TrailingStepSlPoints;
        var target = (decimal)options.TrailingTargetPoints;

        if (gained >= target)
        {
            var stepsAfterTarget = step > 0 ? Math.Floor((gained - target) / step) : 0m;
            return entryPremium + (decimal)options.TrailingTargetLockPoints + stepSl * stepsAfterTarget;
        }

        var stepsBeforeTarget = step > 0 ? Math.Floor(gained / step) : 0m;
        return entryPremium - (decimal)options.TrailingInitialRiskPoints + stepSl * stepsBeforeTarget;
    }

    /// <summary>True once the stop has started moving up from its initial level (used for the "trailing" label).</summary>
    public static bool IsTrailing(decimal entryPremium, decimal peakPremium, StrategyOptions options) =>
        options.TrailingStepPoints > 0 && peakPremium - entryPremium >= (decimal)options.TrailingStepPoints;
}