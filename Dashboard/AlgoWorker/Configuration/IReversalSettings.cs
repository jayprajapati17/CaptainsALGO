namespace AlgoWorker.Configuration;

/// <summary>
/// Everything the Upside Reversal strategy reads (signal engine, seeder, position tracker). Implemented by
/// <see cref="StrategyOptions"/> (Nifty) and <see cref="BankNiftyOptions"/> (Bank Nifty) with identical property names, so the same
/// strategy code runs for either index and the Settings page shows both side by side. All point values are in that index's own points.
/// </summary>
public interface IReversalSettings
{
    bool ReversalStrategyEnabled { get; }
    bool ReversalSetupAEnabled { get; }
    bool ReversalSetupCEnabled { get; }
    string ReversalEarliestEntryTime { get; }
    string ReversalLastEntryTime { get; }
    string ReversalForceExitTime { get; }
    int ReversalEmaFastPeriod { get; }
    int ReversalEmaSlowPeriod { get; }
    int ReversalSeedLookbackTradingDays { get; }
    double ReversalDoubleBottomTolerancePoints { get; }
    double ReversalMinBouncePoints { get; }
    int ReversalConfirmCandles { get; }
    int ReversalMinHigherLows { get; }
    double ReversalSlBufferPoints { get; }
    double ReversalMinRiskReward { get; }
    double ReversalSupportTolerancePoints { get; }
    double ReversalTrendDownPercent { get; }
    int ReversalMaxTradesPerDay { get; }
    int ReversalMaxConsecutiveLosses { get; }
    int PositionUpdateIntervalMinutes { get; }
    int LotSize { get; }
    int StrikeStepPoints { get; }
}