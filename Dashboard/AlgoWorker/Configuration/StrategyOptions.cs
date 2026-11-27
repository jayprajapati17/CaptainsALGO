namespace AlgoWorker.Configuration;

/// <summary>
/// All tunable knobs for the EMA21/50 crossover strategy. Backed by the
/// backtest documented in Nifty_EMA_Alert_Bot_Design.md, Section 3.5 / 3.5.1.
/// </summary>
public sealed class StrategyOptions : IEmaSettings, IReversalSettings
{
    // >>> NEW: EMA strategy's periods and candle timeframe, now configurable
    // instead of hardcoded in IndicatorEngine.cs. The crossover (and the hard
    // EMA50 stop) use EmaMidPeriod vs EmaSlowPeriod -- EmaFastPeriod is tracked
    // but not currently used by any signal/exit logic.
    // NOTE: if you change EmaCandleMinutes, the historical seeder fetches
    // candles at this SAME interval automatically (no separate setting needed) --
    // but double check HistoricalCandleUrlTemplate / IntradayCandleUrlTemplate
    // below still make sense for whatever interval you pick (Upstox supports
    // 1/3/5/10/15/30 minute candles).
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

    // >>> NEW: MACD strategy's periods and candle timeframe, now configurable
    // instead of hardcoded in MacdCalculator.cs / ThreeMinCandleAggregatorService.cs.
    // Same seeding note as above applies -- MacdCandleMinutes drives both the
    // live candle aggregator AND the seeder's fetch interval.
    [Setting("MACD strategy", "", RestartRequired = true)]
    public int MacdFastPeriod { get; set; } = 12;
    [Setting("MACD strategy", "", RestartRequired = true)]
    public int MacdSlowPeriod { get; set; } = 26;
    [Setting("MACD strategy", "", RestartRequired = true)]
    public int MacdSignalPeriod { get; set; } = 9;
    [Setting("MACD strategy", "MACD strategy candle timeframe in minutes.", RestartRequired = true)]
    public int MacdCandleMinutes { get; set; } = 3;

    public const string SectionName = "Strategy";

    /// <summary>Number of 15-min candles a crossover must hold before it's "confirmed" (default 4 = 1 hour).</summary>
    [Setting("EMA strategy", "Candles a crossover must hold before it is confirmed.")]
    public int ConfirmationBars { get; set; } = 4;

    /// <summary>Nifty 50 options lot size.</summary>
    [Setting("General", "Nifty lot size used by the EMA and Breakout strategies.")]
    public int LotSize { get; set; } = 65;

    /// <summary>If false, only Golden Cross (long) signals are alerted.</summary>
    [Setting("EMA strategy")]
    public bool AlertBothSides { get; set; } = true;

    /// <summary>Max ADX movement (points) over AdxFlatLookbackBars to be considered "flat" -> exit trigger.</summary>
    [Setting("EMA strategy")]
    public double AdxFlatThresholdPoints { get; set; } = 1.0;

    [Setting("EMA strategy")]
    public int AdxFlatLookbackBars { get; set; } = 3;

    /// <summary>ADX drop (points) from its post-entry peak that counts as "declining" -> exit trigger.</summary>
    [Setting("EMA strategy")]
    public double AdxDeclineThresholdPoints { get; set; } = 3.0;

    /// <summary>Consecutive falling-ADX candles that also count as "declining" -> exit trigger.</summary>
    [Setting("EMA strategy")]
    public int AdxDeclineConsecutiveBars { get; set; } = 2;

    /// <summary>How often (minutes) to push a "position running" P&amp;L update to Telegram.</summary>
    [Setting("General", "How often a 'position running' update is sent to Telegram.")]
    public int PositionUpdateIntervalMinutes { get; set; } = 15;

    /// <summary>HH:mm, IST. Used as a fallback / sanity bound alongside the live Exchange Status API.</summary>
    [Setting("General", "Market session start (HH:mm, IST).")]
    public string SessionStartTime { get; set; } = "09:15";

    [Setting("General", "Market session end (HH:mm, IST).")]
    public string SessionEndTime { get; set; } = "15:20";

    /// <summary>Trading days of 15-min history to fetch at startup to warm up EMA50/ADX.</summary>
    [Setting("EMA strategy", "", RestartRequired = true)]
    public int SeedLookbackTradingDays { get; set; } = 20;

    /// <summary>Strike spacing for ATM rounding (Nifty = 50).</summary>
    [Setting("General", "Strike spacing used for ATM rounding (Nifty = 50).")]
    public int StrikeStepPoints { get; set; } = 50;

    // >>> NEW: how many strikes in-the-money the CURRENT-WEEK leg should be.
    // Each signal now opens TWO virtual positions: a current-week ITM leg
    // (this) and a next-week ATM leg (uses StrikeStepPoints, offset 0).
    [Setting("EMA strategy", "How many strikes in-the-money the EMA current-week leg is.")]
    public int ItmStrikeDepth { get; set; } = 2;

    // Opening Range Breakout (ORB) strategy (5-min timeframe, intraday only). 
    [Setting("Breakout (ORB) strategy", "", RestartRequired = true)]
    public bool BreakoutStrategyEnabled { get; set; } = true;

    /// <summary>IST time-of-day at which any open breakout position is force-closed (no overnight carry).</summary>
    [Setting("Breakout (ORB) strategy", "Open Breakout positions are force-closed at this time (HH:mm, IST).")]
    public string BreakoutForceExitTime { get; set; } = "15:20";

    /// <summary>Maximum number of ORB trades opened per day (re-entries after a stop-loss count). Signals beyond this are logged as skipped.</summary>
    [Setting("Breakout (ORB) strategy", "Maximum ORB trades opened per day (re-entries after a stop-loss count). Extra signals are logged as skipped.")]
    public int BreakoutMaxTradesPerDay { get; set; } = 3;


    // =====================================================================
    // Nifty 50 LIQUIDITY SWEEP (CE) strategy (5-min, bullish only, buys CE).
    // Sweep: previous candle red; the current candle trades below its low and closes back above it; a confirmation
    // candle then closes above the sweep candle's high. Stop-loss = the previous (red) candle's low.
    // EMA short >= EMA long: hold with the app's existing trailing stop-loss. EMA short < EMA long: hold until Nifty
    // reaches the long EMA; a candle close above it turns the trade into the long hold with the trailing stop-loss.
    // Volume / VWAP / VPVR filters are NOT used (the index feed has no volume).
    // =====================================================================
    [Setting("Liquidity sweep (CE)", "Turn the Liquidity Sweep (CE) strategy on/off. Changes apply to new signals; an open trade keeps running.")]
    public bool ReversalStrategyEnabled { get; set; } = true;

    /// <summary>No entries before this time (HH:mm) -- avoids the first 15 min of opening volatility.</summary>
    [Setting("Liquidity sweep (CE)", "No new entries before this time (HH:mm, IST).")]
    public string ReversalEarliestEntryTime { get; set; } = "09:30";
    /// <summary>No new entries after this time (HH:mm).</summary>
    [Setting("Liquidity sweep (CE)", "No new entries after this time (HH:mm, IST).")]
    public string ReversalLastEntryTime { get; set; } = "14:45";
    /// <summary>Open positions are force-closed at this time (HH:mm, IST).</summary>
    [Setting("Liquidity sweep (CE)", "Open trades are closed at this time (HH:mm, IST). They are never carried overnight.")]
    public string ReversalForceExitTime { get; set; } = "15:20";

    [Setting("Liquidity sweep (CE)", "Short EMA period on the 5-min chart. EMA short above EMA long means: hold with the trailing stop-loss (restart needed).", RestartRequired = true)]
    public int ReversalEmaShortPeriod { get; set; } = 9;
    [Setting("Liquidity sweep (CE)", "Long EMA period on the 5-min chart. When EMA short is below it, the trade is held until Nifty reaches this EMA (restart needed).", RestartRequired = true)]
    public int ReversalEmaLongPeriod { get; set; } = 21;
    [Setting("Liquidity sweep (CE)", "Trend EMA period, used only by the strong-down-trend-day filter (restart needed).", RestartRequired = true)]
    public int ReversalEmaTrendPeriod { get; set; } = 50;
    [Setting("Liquidity sweep (CE)", "'Touch / near' distance: Nifty counts as reaching the long EMA when it is within this many points below it.")]
    public double ReversalEmaTouchTolerancePoints { get; set; } = 3.0;
    [Setting("Liquidity sweep (CE)", "On reaching the long EMA, wait for the 5-min candle close: closes above it = keep holding with the trailing stop-loss, otherwise exit. Off = exit immediately on touch.")]
    public bool ReversalWaitForCandleCloseAtEma { get; set; } = true;
    [Setting("Liquidity sweep (CE)", "Previous trading days of 5-min candles replayed at startup.", RestartRequired = true)]
    public int ReversalSeedLookbackTradingDays { get; set; } = 2;

    /// <summary>Max candles to wait for the confirmation candle after the sweep candle.</summary>
    [Setting("Liquidity sweep (CE)", "Maximum candles to wait for confirmation after the sweep candle (3-4 suggested).")]
    public int ReversalConfirmCandles { get; set; } = 4;
    /// <summary>Minimum candles in the window with a low above the sweep low (higher lows).</summary>
    [Setting("Liquidity sweep (CE)", "Minimum candles in the confirmation window with a low above the sweep candle's low.")]
    public int ReversalMinHigherLows { get; set; } = 1;
    /// <summary>Stop-loss buffer (points) below the previous (red) candle's low. 0 = exactly at the low.</summary>
    [Setting("Liquidity sweep (CE)", "Stop-loss buffer in Nifty points below the previous (red) candle's low. 0 = stop-loss exactly at that low.")]
    public double ReversalSlBufferPoints { get; set; } = 0.0;
    /// <summary>The sweep low must be within this many points of the previous-day low or today's low (support zone).</summary>
    [Setting("Liquidity sweep (CE)", "Counter-trend entries only (EMA short below EMA long): the sweep low must be within this many Nifty points of the previous-day low or today's low.")]
    public double ReversalSupportTolerancePoints { get; set; } = 25.0;
    /// <summary>Skip when EMA long &lt; EMA trend AND price is this % below today's open (strong down-trend day). 0 = filter off.</summary>
    [Setting("Liquidity sweep (CE)", "Skip when the long EMA is below the trend EMA and Nifty is this % below today's open (strong down-trend day). 0 = filter off.")]
    public double ReversalTrendDownPercent { get; set; } = 0.75;

    [Setting("Liquidity sweep (CE)", "Maximum trades opened per day.")]
    public int ReversalMaxTradesPerDay { get; set; } = 3;
    /// <summary>No more trades for the day after this many consecutive losses.</summary>
    [Setting("Liquidity sweep (CE)", "No more trades for the day after this many consecutive losses.")]
    public int ReversalMaxConsecutiveLosses { get; set; } = 2;

    // >>> CHANGED (step-trailing rule): all three strategies (EMA, Breakout, MACD)
    // share ONE stop-loss rule -- see TrailingStopCalculator.cs for the formula and a
    // worked example. This is IN ADDITION TO each strategy's own other exit rules
    // (EMA's ADX-flat/decline and opposite-crossover, Breakout's/MACD's intraday
    // cutoff, MACD's opposite-crossover) -- whichever exit triggers first wins.

    /// <summary>Initial stop-loss, in premium points, below entry.</summary>
    [Setting("Stop-loss / trailing", "Initial stop-loss, in premium points below entry.")]
    public double TrailingInitialRiskPoints { get; set; } = 15.0;

    /// <summary>Every time the peak premium moves this many points above entry (cumulatively), the stop moves up by TrailingStepSlPoints.</summary>
    [Setting("Stop-loss / trailing", "For every this-many points the price moves up from entry...")]
    public double TrailingStepPoints { get; set; } = 5.0;

    /// <summary>How many points the stop-loss moves up per TrailingStepPoints of favourable move.</summary>
    [Setting("Stop-loss / trailing", "...the stop-loss moves up by this many points.")]
    public double TrailingStepSlPoints { get; set; } = 3.0;

    /// <summary>First target, in points above entry. On reaching it the stop jumps to Entry + TrailingTargetLockPoints (the position is NOT closed); also triggers the MACD "target hit" alert.</summary>
    [Setting("Stop-loss / trailing", "First target (points above entry). The position is not closed here; the stop jumps instead.")]
    public double TrailingTargetPoints { get; set; } = 30.0;

    /// <summary>Stop-loss (points ABOVE entry) once the target is reached; after that it keeps stepping up by TrailingStepSlPoints per TrailingStepPoints.</summary>
    [Setting("Stop-loss / trailing", "When the target is reached the stop moves to Entry + this many points, then keeps stepping.")]
    public double TrailingTargetLockPoints { get; set; } = 20.0;

    /// <summary>After the target: for every this-many extra points gained the stop steps up again. 0 = same as TrailingStepPoints (the old Nifty behaviour).</summary>
    [Setting("Stop-loss / trailing", "After the target: for every this-many extra points gained... (0 = same as the step above)")]
    public double TrailingPostStepPoints { get; set; } = 0.0;

    /// <summary>After the target: how far the stop steps up per TrailingPostStepPoints. 0 = same as TrailingStepSlPoints.</summary>
    [Setting("Stop-loss / trailing", "...the stop-loss moves up by this many points. (0 = same as the step above)")]
    public double TrailingPostStepSlPoints { get; set; } = 0.0;

    // >>> NEW: 3-Minute MACD (12/26/9) Intraday Strategy
    // (per "3-Minute MACD & Dynamic Trailing SL Strategy" doc). Nifty only.
    [Setting("MACD strategy", "", RestartRequired = true)]
    public bool MacdStrategyEnabled { get; set; } = true;

    /// <summary>Nifty 50 lot size for this strategy (same as the other two strategies).</summary>
    [Setting("MACD strategy", "Nifty lot size used by the MACD strategy.")]
    public int MacdLotSize { get; set; } = 65;

    /// <summary>IST time-of-day at which any open MACD position is force-closed (no overnight carry, same intraday-only philosophy as the breakout strategy).</summary>
    [Setting("MACD strategy", "Open MACD positions are force-closed at this time (HH:mm, IST).")]
    public string MacdForceExitTime { get; set; } = "15:20";

    /// <summary>How many previous TRADING days of 3-min candles to replay at startup so MACD(12,26,9) is already warmed up (Signal line needs ~35 candles).</summary>
    [Setting("MACD strategy", "", RestartRequired = true)]
    public int MacdSeedLookbackTradingDays { get; set; } = 2;

    // >>> NEW: CPR (Central Pivot Range) end-of-day info alert thresholds.
    // CPR width as a % of close below this -> "Narrow" (trending-day read); above the wide one -> "Wide" (range-bound read).
    [Setting("CPR")]
    public double CprNarrowThresholdPercent { get; set; } = 0.15;
    [Setting("CPR")]
    public double CprWideThresholdPercent { get; set; } = 0.40;
}