namespace AlgoWorker.Models;

/// <summary>
/// Central Pivot Range + standard pivot support/resistance levels, computed
/// from a completed trading day's High/Low/Close -- valid for the NEXT
/// trading day. Sent as an end-of-day (~15:30) Telegram alert.
///
/// NOTE: the single source of truth for COMPUTING these levels is
/// CprAnalysisService.Compute() (it reads thresholds from StrategyOptions /
/// appsettings.json). This record intentionally has NO factory method of its
/// own anymore -- an earlier draft had a FromPreviousDayOhlc(...) method here
/// with its own HARDCODED thresholds (0.4 / 0.8) that silently diverged from
/// the configured CprNarrowThresholdPercent/CprWideThresholdPercent (0.15 /
/// 0.40) used by the actual service. That duplication was a real bug risk --
/// removed rather than kept as unused dead code.
/// </summary>
public sealed record CprLevels(
    DateOnly ForTradingDay,       // the day these levels apply TO (tomorrow)
    decimal SourceHigh,
    decimal SourceLow,
    decimal SourceClose,
    decimal Pivot,
    decimal Tc,
    decimal Bc,
    decimal R1,
    decimal S1,
    decimal R2,
    decimal S2,
    double WidthPercent,
    string Reading,     // "Narrow CPR", "Wide CPR", "Normal CPR"
    string BiasNote)     // qualitative close-vs-pivot lean
{
    /// <summary>The width of the CPR in absolute points (TC - BC).</summary>
    public decimal Width => Tc - Bc;

    /// <summary>
    /// CPR width as a percentage of an arbitrary price (e.g. tomorrow's actual
    /// open, once known) -- NOT necessarily the same as the stored
    /// <see cref="WidthPercent"/>, which was computed against SourceClose at
    /// the time these levels were built. Useful if you want to re-express the
    /// same absolute width relative to a different, more current price.
    /// </summary>
    public decimal WidthPercentOf(decimal price)
    {
        if (price == 0) return 0;
        return (Width / price) * 100m;
    }
}