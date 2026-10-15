using AlgoWorker.Configuration;

namespace AlgoWorker.Services;

/// <summary>
/// All the numbers of the step-trailing stop-loss rule, in premium points. One struct so Nifty
/// (StrategyOptions) and Bank Nifty (BankNiftyOptions) feed the SAME formula:
///   gained = Peak - Entry
///   gained &lt; Target:   SL = Entry - InitialRisk + StepSl x floor(gained / Step)
///   gained &gt;= Target:  SL = Entry + Lock + PostStepSl x floor((gained - Target) / PostStep)
/// Both indices now expose the same seven settings; a Nifty post-target value of 0 falls back to Step/StepSl.
/// </summary>
public readonly record struct TrailingParams(
    decimal InitialRisk, decimal Step, decimal StepSl, decimal Target, decimal Lock, decimal PostStep, decimal PostStepSl)
{
    /// <summary>The same seven settings for either index. A post-target value of 0 (or less) means "same as before the target".</summary>
    public static TrailingParams From(ITrailingSettings o)
    {
        var step = (decimal)o.TrailingStepPoints;
        var stepSl = (decimal)o.TrailingStepSlPoints;
        var postStep = o.TrailingPostStepPoints > 0 ? (decimal)o.TrailingPostStepPoints : step;
        var postStepSl = o.TrailingPostStepSlPoints > 0 ? (decimal)o.TrailingPostStepSlPoints : stepSl;
        return new((decimal)o.TrailingInitialRiskPoints, step, stepSl,
            (decimal)o.TrailingTargetPoints, (decimal)o.TrailingTargetLockPoints, postStep, postStepSl);
    }
}

public static class TrailingStop
{
    /// <summary>The stop-loss premium. Never decreases as <paramref name="peakPremium"/> rises, so it is safe to recompute on every tick.</summary>
    public static decimal Compute(decimal entryPremium, decimal peakPremium, TrailingParams p)
    {
        var gained = Math.Max(0m, peakPremium - entryPremium);

        if (gained >= p.Target)
        {
            var steps = p.PostStep > 0 ? Math.Floor((gained - p.Target) / p.PostStep) : 0m;
            return entryPremium + p.Lock + p.PostStepSl * steps;
        }

        var stepsBefore = p.Step > 0 ? Math.Floor(gained / p.Step) : 0m;
        return entryPremium - p.InitialRisk + p.StepSl * stepsBefore;
    }

    /// <summary>True once the stop has started moving up from its initial level (the "trailing" label).</summary>
    public static bool IsTrailing(decimal entryPremium, decimal peakPremium, TrailingParams p) =>
        p.Step > 0 && peakPremium - entryPremium >= p.Step;
}