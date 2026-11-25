namespace AlgoWorker.Configuration;

/// <summary>
/// Settings of the EMA 9/21 options strategies (config section "EmaCross", editable from the Dashboard's Settings page).
/// Strategy A trades 15-minute candles (80-point index stop, CPR filter); Strategy B trades 1-hour candles (150-point stop,
/// breakeven at 1R, trailing from 2R, optional ADX-rising filter). The signal comes from the Nifty INDEX; the trade is a
/// bought CE/PE chosen by premium. Paper trading only -- no orders are ever sent.
/// Everything that shapes the strategy engine itself is read once at startup (RestartRequired).
/// </summary>
public sealed class EmaCrossOptions
{
    public const string SectionName = "EmaCross";

    // ---- General ----
    [Setting("General", "Master switch for the EMA 9/21 strategies (restart needed).", RestartRequired = true)]
    public bool Enabled { get; set; } = true;
    [Setting("General", "Run Strategy A (15-min candles, CPR filter) (restart needed).", RestartRequired = true)]
    public bool StrategyAEnabled { get; set; } = true;
    [Setting("General", "Run Strategy B (1-hour candles, breakeven + trailing) (restart needed).", RestartRequired = true)]
    public bool StrategyBEnabled { get; set; } = true;
    [Setting("General", "Nifty lot size used for P&L (the guide says to read it from the instrument master -- keep this in step with the exchange).")]
    public int LotSize { get; set; } = 65;
    [Setting("General", "Fast EMA period (restart needed).", RestartRequired = true)]
    public int FastPeriod { get; set; } = 9;
    [Setting("General", "Slow EMA period (restart needed).", RestartRequired = true)]
    public int SlowPeriod { get; set; } = 21;
    [Setting("General", "Closed candles replayed at startup so the EMAs match the backtest (restart needed).", RestartRequired = true)]
    public int HistoryCandles { get; set; } = 500;
    [Setting("General", "Candle polling starts at this time (HH:mm, IST).", RestartRequired = true)]
    public string SessionStartTime { get; set; } = "09:15";
    [Setting("General", "Candle polling stops after this time (HH:mm, IST). The last candle (15:15-15:30) needs 15:30.", RestartRequired = true)]
    public string SessionEndTime { get; set; } = "15:30";

    // ---- Option selection (not backtested -- tune freely) ----
    [Setting("Option selection", "Lowest option premium accepted at entry.")]
    public double PremiumMin { get; set; } = 130;
    [Setting("Option selection", "Highest option premium accepted at entry.")]
    public double PremiumMax { get; set; } = 150;
    [Setting("Option selection", "Premium to aim for: the strike closest to this inside the range is chosen.")]
    public double PremiumTargetMid { get; set; } = 140;
    [Setting("Option selection", "Roll to the next expiry when the open option has this many calendar days (or fewer) left.")]
    public int RollWhenDaysToExpiryAtMost { get; set; } = 2;

    // ---- Strategy A (15-minute) ----
    [Setting("Strategy A (15-min)", "Index stop-loss in points (restart needed).", RestartRequired = true)]
    public double StrategyAStopPoints { get; set; } = 80;
    [Setting("Strategy A (15-min)", "CE only above the CPR, PE only below it (restart needed).", RestartRequired = true)]
    public bool StrategyAUseCprFilter { get; set; } = true;
    [Setting("Strategy A (15-min)", "Pick the nearest expiry with at least this many trading days left at entry.")]
    public int StrategyAMinTradingDaysToExpiry { get; set; } = 3;
    [Setting("Strategy A (15-min)", "Safety cap: exit if the option premium falls this % below entry (not backtested).")]
    public double StrategyAPremiumStopPercent { get; set; } = 35;

    // ---- Strategy B (1-hour) ----
    [Setting("Strategy B (1-hour)", "Index stop-loss in points (restart needed).", RestartRequired = true)]
    public double StrategyBStopPoints { get; set; } = 150;
    [Setting("Strategy B (1-hour)", "Move the stop to entry once the profit reaches this many R (0 = off) (restart needed).", RestartRequired = true)]
    public double StrategyBBreakevenAtR { get; set; } = 1.0;
    [Setting("Strategy B (1-hour)", "Start trailing once the profit reaches this many R (0 = off) (restart needed).", RestartRequired = true)]
    public double StrategyBTrailStartR { get; set; } = 2.0;
    [Setting("Strategy B (1-hour)", "Trailing distance behind the best price, in R (restart needed).", RestartRequired = true)]
    public double StrategyBTrailDistanceR { get; set; } = 1.0;
    [Setting("Strategy B (1-hour)", "Only enter when the signal candle's ADX is above the previous candle's (restart needed).", RestartRequired = true)]
    public bool StrategyBUseAdxRisingFilter { get; set; } = false;
    [Setting("Strategy B (1-hour)", "ADX period. The backtest file does not say which one it used -- compare with the file's ADX column (restart needed).", RestartRequired = true)]
    public int StrategyBAdxPeriod { get; set; } = 14;
    [Setting("Strategy B (1-hour)", "Pick the nearest expiry with at least this many calendar days left at entry.")]
    public int StrategyBMinCalendarDaysToExpiry { get; set; } = 8;
    [Setting("Strategy B (1-hour)", "Safety cap: exit if the option premium falls this % below entry (not backtested).")]
    public double StrategyBPremiumStopPercent { get; set; } = 45;
}
