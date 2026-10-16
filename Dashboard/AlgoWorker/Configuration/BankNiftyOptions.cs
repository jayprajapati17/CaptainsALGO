namespace AlgoWorker.Configuration;

/// <summary>
/// All Bank Nifty settings (config section "BankNifty", editable from the Dashboard's Settings page --
/// every public get/set property of type string/int/double/bool becomes an AppSettings row).
/// The ORB strategy follows the same rules as Nifty's (current-expiry ATM, trailing SL, force-exit time, max trades/day).
/// </summary>
public sealed class BankNiftyOptions : IEmaSettings, IReversalSettings
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

    // ---- Upside Reversal strategy (same rules and key names as Nifty's; point values scaled for Bank Nifty, ~2.4x) ----
    [Setting("Upside Reversal strategy", "Turn the Upside Reversal strategy on/off.")]
    public bool ReversalStrategyEnabled { get; set; } = true;
    [Setting("Upside Reversal strategy", "Setup A: Double Bottom.")]
    public bool ReversalSetupAEnabled { get; set; } = true;
    [Setting("Upside Reversal strategy", "Setup C: Liquidity Sweep + confirmation window.")]
    public bool ReversalSetupCEnabled { get; set; } = true;
    [Setting("Upside Reversal strategy", "No reversal entries before this time (HH:mm, IST).")]
    public string ReversalEarliestEntryTime { get; set; } = "09:30";
    [Setting("Upside Reversal strategy", "No new reversal entries after this time (HH:mm, IST).")]
    public string ReversalLastEntryTime { get; set; } = "14:45";
    [Setting("Upside Reversal strategy", "Open Reversal positions are force-closed at this time (HH:mm, IST).")]
    public string ReversalForceExitTime { get; set; } = "15:20";
    [Setting("Upside Reversal strategy", "Fast EMA period on the 5-min chart (price must be below it for a setup).", RestartRequired = true)]
    public int ReversalEmaFastPeriod { get; set; } = 21;
    [Setting("Upside Reversal strategy", "Slow EMA period on the 5-min chart (used by the down-trend-day filter).", RestartRequired = true)]
    public int ReversalEmaSlowPeriod { get; set; } = 50;
    [Setting("Upside Reversal strategy", "Previous trading days of 5-min candles replayed at startup.", RestartRequired = true)]
    public int ReversalSeedLookbackTradingDays { get; set; } = 2;
    [Setting("Upside Reversal strategy", "Setup A: second low must be within +/- this many points of the first low.")]
    public double ReversalDoubleBottomTolerancePoints { get; set; } = 24.0;
    [Setting("Upside Reversal strategy", "Setup A: minimum bounce (points) between the two lows.")]
    public double ReversalMinBouncePoints { get; set; } = 72.0;
    [Setting("Upside Reversal strategy", "Setup C: maximum candles to wait for confirmation (3-4 suggested).")]
    public int ReversalConfirmCandles { get; set; } = 4;
    [Setting("Upside Reversal strategy", "Setup C: minimum candles in the window with a low above the sweep low.")]
    public int ReversalMinHigherLows { get; set; } = 1;
    [Setting("Upside Reversal strategy", "Stop-loss buffer in index points below the setup low.")]
    public double ReversalSlBufferPoints { get; set; } = 17.0;
    [Setting("Upside Reversal strategy", "Minimum reward:risk to T2. Setups below this are skipped.")]
    public double ReversalMinRiskReward { get; set; } = 2.0;
    [Setting("Upside Reversal strategy", "Setup low must be within this many index points of the previous-day low or today's low.")]
    public double ReversalSupportTolerancePoints { get; set; } = 60.0;
    [Setting("Upside Reversal strategy", "Skip when EMA fast < EMA slow and the index is this % below today's open (strong down-trend day). 0 = filter off.")]
    public double ReversalTrendDownPercent { get; set; } = 0.75;
    [Setting("Upside Reversal strategy", "Maximum Reversal trades opened per day.")]
    public int ReversalMaxTradesPerDay { get; set; } = 3;
    [Setting("Upside Reversal strategy", "No more Reversal trades for the day after this many consecutive losses.")]
    public int ReversalMaxConsecutiveLosses { get; set; } = 2;

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