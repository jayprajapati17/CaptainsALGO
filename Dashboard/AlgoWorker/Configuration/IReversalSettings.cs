namespace AlgoWorker.Configuration;

/// <summary>
/// Everything the Liquidity Sweep (CE) strategy reads (signal engine, seeder, position tracker). Implemented by
/// <see cref="StrategyOptions"/> (Nifty). All point values are in Nifty points. The trailing stop-loss numbers come from
/// <see cref="ITrailingSettings"/> (the app's existing rule).
/// </summary>
public interface IReversalSettings : ITrailingSettings
{
    bool ReversalStrategyEnabled { get; }
    string ReversalEarliestEntryTime { get; }
    string ReversalLastEntryTime { get; }
    string ReversalForceExitTime { get; }
    int ReversalEmaShortPeriod { get; }
    int ReversalEmaLongPeriod { get; }
    int ReversalEmaTrendPeriod { get; }
    double ReversalEmaTouchTolerancePoints { get; }
    bool ReversalWaitForCandleCloseAtEma { get; }
    int ReversalSeedLookbackTradingDays { get; }
    int ReversalConfirmCandles { get; }
    int ReversalMinHigherLows { get; }
    double ReversalSlBufferPoints { get; }
    double ReversalSupportTolerancePoints { get; }
    double ReversalTrendDownPercent { get; }
    int ReversalMaxTradesPerDay { get; }
    int ReversalMaxConsecutiveLosses { get; }
    int PositionUpdateIntervalMinutes { get; }
    int LotSize { get; }
    int StrikeStepPoints { get; }
}
