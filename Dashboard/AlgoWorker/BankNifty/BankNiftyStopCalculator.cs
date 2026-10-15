using AlgoWorker.Configuration;

namespace AlgoWorker.BankNifty;
using AlgoWorker.Services;

/// <summary>
/// Bank Nifty step-trailing stop-loss ("gained" = PeakPremium - EntryPremium, all numbers configurable):
///   gained &lt; Target:   SL = Entry - InitialRisk + StepSl x floor(gained / Step)
///   gained &gt;= Target:  SL = Entry + Lock + PostStepSl x floor((gained - Target) / PostStep)
/// Defaults (30 / 10 / 5 / 50 / 25 / 5 / 5): peak +0 -> SL -30, +10 -> -25, +40 -> -10, +49 -> -10,
/// +50 -> +25, +55 -> +30, +60 -> +35 ... The result never decreases as the peak rises, so it can be
/// recomputed on every tick.
/// </summary>
public static class BankNiftyStopCalculator
{
    public static decimal ComputeStopLoss(decimal entryPremium, decimal peakPremium, BankNiftyOptions o)
    {
        return Math.Round(TrailingStop.Compute(entryPremium, peakPremium, TrailingParams.From(o)), 2);
    }

    /// <summary>True once the stop has started moving up from its initial level.</summary>
    public static bool IsTrailing(decimal entryPremium, decimal peakPremium, BankNiftyOptions o) =>
        TrailingStop.IsTrailing(entryPremium, peakPremium, TrailingParams.From(o));
}
