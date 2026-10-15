namespace AlgoWorker.Configuration;

/// <summary>The seven numbers of the step-trailing stop-loss rule. Both StrategyOptions (Nifty) and BankNiftyOptions expose them under the same names.</summary>
public interface ITrailingSettings
{
    double TrailingInitialRiskPoints { get; }
    double TrailingStepPoints { get; }
    double TrailingStepSlPoints { get; }
    double TrailingTargetPoints { get; }
    double TrailingTargetLockPoints { get; }
    /// <summary>After the target: points gained per further step. 0 = same as <see cref="TrailingStepPoints"/>.</summary>
    double TrailingPostStepPoints { get; }
    /// <summary>After the target: stop-loss move per further step. 0 = same as <see cref="TrailingStepSlPoints"/>.</summary>
    double TrailingPostStepSlPoints { get; }
}

/// <summary>
/// Everything the EMA21/50 + ADX strategy reads (signal engine, indicator engine, seeder, position tracker). Implemented by
/// <see cref="StrategyOptions"/> (Nifty) and <see cref="BankNiftyOptions"/> (Bank Nifty) with identical property names, so the same
/// strategy code runs for either index and the Settings page shows both side by side.
/// </summary>
public interface IEmaSettings : ITrailingSettings
{
    bool EmaStrategyEnabled { get; }
    int EmaFastPeriod { get; }
    int EmaMidPeriod { get; }
    int EmaSlowPeriod { get; }
    int AdxPeriod { get; }
    int EmaCandleMinutes { get; }
    int ConfirmationBars { get; }
    bool AlertBothSides { get; }
    double AdxFlatThresholdPoints { get; }
    int AdxFlatLookbackBars { get; }
    double AdxDeclineThresholdPoints { get; }
    int AdxDeclineConsecutiveBars { get; }
    int SeedLookbackTradingDays { get; }
    int PositionUpdateIntervalMinutes { get; }
    int LotSize { get; }
    int StrikeStepPoints { get; }
    int ItmStrikeDepth { get; }
}