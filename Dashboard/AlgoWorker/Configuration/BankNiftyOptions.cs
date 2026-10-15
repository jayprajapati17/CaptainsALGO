namespace AlgoWorker.Configuration;

/// <summary>
/// All Bank Nifty settings (config section "BankNifty", editable from the Dashboard's Settings page --
/// every public get/set property of type string/int/double/bool becomes an AppSettings row).
/// The ORB strategy follows the same rules as Nifty's (current-expiry ATM, trailing SL, force-exit time, max trades/day).
/// </summary>
public sealed class BankNiftyOptions : IEmaSettings
{
    public const string SectionName = "BankNifty";

    // ---- General ----
    [Setting("General", "Master switch for the Bank Nifty worker (restart needed).", RestartRequired = true)]
    public bool Enabled { get; set; } = true;
    [Setting("General", "Bank Nifty lot size (quantity per trade).")]
    public int LotSize { get; set; } = 30;
    [Setting("General", "Worker starts tracking at this time (HH:mm, IST).", RestartRequired = true)]
    public string SessionStartTime { get; set; } = "09:15";
    [Setting("General", "Worker stops tracking at this time (HH:mm, IST).", RestartRequired = true)]
    public string SessionEndTime { get; set; } = "15:20";
    [Setting("General", "How often a 'position running' update is sent to Telegram.")]
    public int PositionUpdateIntervalMinutes { get; set; } = 3;

    // ---- Strike selection (same as Nifty: current expiry, ATM) ----
    [Setting("General", "Strike spacing in index points; the ATM strike is spot rounded to this (Bank Nifty = 100).")]
    public int StrikeStepPoints { get; set; } = 100;

    // ---- ORB strategy (same rules and key names as the Nifty "Breakout (ORB)" strategy) ----
    [Setting("Breakout (ORB) strategy", "Turn the Opening Range Breakout strategy on/off.", RestartRequired = true)]
    public bool BreakoutStrategyEnabled { get; set; } = true;

    /// <summary>IST time-of-day at which an open ORB position is force-closed (no overnight carry).</summary>
    [Setting("Breakout (ORB) strategy", "Open ORB positions are force-closed at this time (HH:mm, IST).")]
    public string BreakoutForceExitTime { get; set; } = "15:20";

    /// <summary>Maximum number of ORB trades opened per day (re-entries after a stop-loss count). Extra signals are logged as skipped.</summary>
    [Setting("Breakout (ORB) strategy", "Maximum ORB trades opened per day (re-entries after a stop-loss count). Extra signals are logged as skipped.")]
    public int BreakoutMaxTradesPerDay { get; set; } = 3;

    // ---- EMA21/50 + ADX crossover strategy (same rules and key names as Nifty's "EMA strategy") ----
    [Setting("EMA strategy", "Turn the EMA21/50 crossover strategy on/off.")]
    public bool EmaStrategyEnabled { get; set; } = true;
    [Setting("EMA strategy", "Fast EMA period (tracked, not used by signals).", RestartRequired = true)]
    public int EmaFastPeriod { get; set; } = 10;
    [Setting("EMA strategy", "Mid EMA period -- the line that crosses the slow EMA.", RestartRequired = true)]
    public int EmaMidPeriod { get; set; } = 21;
    [Setting("EMA strategy", "Slow EMA period.", RestartRequired = true)]
    public int EmaSlowPeriod { get; set; } = 50;
    [Setting("EMA strategy", "ADX period.", RestartRequired = true)]
    public int AdxPeriod { get; set; } = 14;
    [Setting("EMA strategy", "EMA strategy candle timeframe in minutes.", RestartRequired = true)]
    public int EmaCandleMinutes { get; set; } = 15;
    [Setting("EMA strategy", "Candles a crossover must hold before it is confirmed.")]
    public int ConfirmationBars { get; set; } = 4;
    [Setting("EMA strategy")]
    public bool AdxRisingFilter { get; set; } = true;
    [Setting("EMA strategy")]
    public bool AlertBothSides { get; set; } = true;
    [Setting("EMA strategy")]
    public double AdxFlatThresholdPoints { get; set; } = 1.0;
    [Setting("EMA strategy")]
    public int AdxFlatLookbackBars { get; set; } = 3;
    [Setting("EMA strategy")]
    public double AdxDeclineThresholdPoints { get; set; } = 3.0;
    [Setting("EMA strategy")]
    public int AdxDeclineConsecutiveBars { get; set; } = 2;
    [Setting("EMA strategy", "", RestartRequired = true)]
    public int SeedLookbackTradingDays { get; set; } = 20;
    [Setting("EMA strategy", "How many strikes in-the-money the EMA current-week leg is.")]
    public int ItmStrikeDepth { get; set; } = 2;

    // ---- Trailing stop-loss (premium points) ----
    // Before the target: SL = Entry - InitialRisk + StepSl x floor(gained / Step)
    // At/after the target: SL = Entry + Lock + PostStepSl x floor((gained - Target) / PostStep)
    [Setting("Stop-loss / trailing", "Initial stop-loss, in premium points below entry.")]
    public double TrailingInitialRiskPoints { get; set; } = 30.0;
    [Setting("Stop-loss / trailing", "For every this-many points gained (before the target)...")]
    public double TrailingStepPoints { get; set; } = 10.0;
    [Setting("Stop-loss / trailing", "...the stop-loss moves up by this many points.")]
    public double TrailingStepSlPoints { get; set; } = 5.0;
    [Setting("Stop-loss / trailing", "Points gained at which the stop jumps to the lock level.")]
    public double TrailingTargetPoints { get; set; } = 50.0;
    [Setting("Stop-loss / trailing", "At the target the stop moves to Entry + this many points.")]
    public double TrailingTargetLockPoints { get; set; } = 25.0;
    [Setting("Stop-loss / trailing", "After the target: for every this-many extra points gained...")]
    public double TrailingPostStepPoints { get; set; } = 5.0;
    [Setting("Stop-loss / trailing", "...the stop-loss moves up by this many points.")]
    public double TrailingPostStepSlPoints { get; set; } = 5.0;
}