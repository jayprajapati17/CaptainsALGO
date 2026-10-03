namespace AlgoWorker.Configuration;

/// <summary>
/// All tunable knobs for the EMA21/50 crossover strategy. Backed by the
/// backtest documented in Nifty_EMA_Alert_Bot_Design.md, Section 3.5 / 3.5.1.
/// </summary>
public sealed class StrategyOptions
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
    public int EmaFastPeriod { get; set; } = 10;
    public int EmaMidPeriod { get; set; } = 21;
    public int EmaSlowPeriod { get; set; } = 50;
    public int AdxPeriod { get; set; } = 14;
    public int EmaCandleMinutes { get; set; } = 15;

    // >>> NEW: MACD strategy's periods and candle timeframe, now configurable
    // instead of hardcoded in MacdCalculator.cs / ThreeMinCandleAggregatorService.cs.
    // Same seeding note as above applies -- MacdCandleMinutes drives both the
    // live candle aggregator AND the seeder's fetch interval.
    public int MacdFastPeriod { get; set; } = 12;
    public int MacdSlowPeriod { get; set; } = 26;
    public int MacdSignalPeriod { get; set; } = 9;
    public int MacdCandleMinutes { get; set; } = 3;

    public const string SectionName = "Strategy";

    /// <summary>Number of 15-min candles a crossover must hold before it's "confirmed" (default 4 = 1 hour).</summary>
    public int ConfirmationBars { get; set; } = 4;

    /// <summary>Require ADX(now) > ADX(N candles ago) at confirmation time for HIGH-CONFIDENCE long signals.</summary>
    public bool AdxRisingFilter { get; set; } = true;

    /// <summary>Nifty 50 options lot size.</summary>
    public int LotSize { get; set; } = 65;

    /// <summary>If false, only Golden Cross (long) signals are alerted.</summary>
    public bool AlertBothSides { get; set; } = true;

    /// <summary>Max ADX movement (points) over AdxFlatLookbackBars to be considered "flat" -> exit trigger.</summary>
    public double AdxFlatThresholdPoints { get; set; } = 1.0;

    public int AdxFlatLookbackBars { get; set; } = 3;

    /// <summary>ADX drop (points) from its post-entry peak that counts as "declining" -> exit trigger.</summary>
    public double AdxDeclineThresholdPoints { get; set; } = 3.0;

    /// <summary>Consecutive falling-ADX candles that also count as "declining" -> exit trigger.</summary>
    public int AdxDeclineConsecutiveBars { get; set; } = 2;

    /// <summary>How often (minutes) to push a "position running" P&amp;L update to Telegram.</summary>
    public int PositionUpdateIntervalMinutes { get; set; } = 15;

    /// <summary>HH:mm, IST. Used as a fallback / sanity bound alongside the live Exchange Status API.</summary>
    public string SessionStartTime { get; set; } = "09:15";

    public string SessionEndTime { get; set; } = "15:40";

    /// <summary>Trading days of 15-min history to fetch at startup to warm up EMA50/ADX.</summary>
    public int SeedLookbackTradingDays { get; set; } = 20;

    /// <summary>Strike spacing for ATM rounding (Nifty = 50).</summary>
    public int StrikeStepPoints { get; set; } = 50;

    // >>> NEW: how many strikes in-the-money the CURRENT-WEEK leg should be.
    // Each signal now opens TWO virtual positions: a current-week ITM leg
    // (this) and a next-week ATM leg (uses StrikeStepPoints, offset 0).
    public int ItmStrikeDepth { get; set; } = 2;

    // >>> NEW: Previous-Day High/Low Breakout strategy (5-min timeframe, intraday only).
    public bool BreakoutStrategyEnabled { get; set; } = true;

    /// <summary>IST time-of-day at which any open breakout position is force-closed (no overnight carry).</summary>
    public string BreakoutForceExitTime { get; set; } = "15:20";

    // >>> CHANGED ("Final Updated Trailing Rule"): all three strategies (EMA,
    // Breakout, MACD) now share ONE common 3-phase stop-loss rule -- see
    // TrailingStopCalculator.cs for the exact formula and a worked example.
    // This replaces each strategy's previous separate SL/trailing config
    // (Breakout's 10/10/3, EMA's 10/10/3, MACD's 10/10/5-step). This is IN
    // ADDITION TO each strategy's own other exit rules (EMA's ADX-flat/decline
    // and opposite-crossover, Breakout's/MACD's intraday cutoff, MACD's
    // opposite-crossover) -- whichever exit condition triggers first wins.

    /// <summary>Phase 1 (initial) stop-loss, in premium points, below entry.</summary>
    public double TrailingInitialRiskPoints { get; set; } = 15.0;

    /// <summary>Once profit reaches this many points, the stop jumps to TrailingPhase2RiskPoints below entry (Phase 2).</summary>
    public double TrailingPhase2TriggerPoints { get; set; } = 15.0;

    /// <summary>Phase 2's flat stop-loss, in premium points, below entry.</summary>
    public double TrailingPhase2RiskPoints { get; set; } = 5.0;

    /// <summary>Once profit reaches this many points, the stop switches to continuous trailing (Phase 3): Peak - TrailingContinuousGapPoints.</summary>
    public double TrailingPhase3TriggerPoints { get; set; } = 25.0;

    /// <summary>Phase 3's trailing gap, in premium points, behind the highest premium seen since entry (ratchets up only).</summary>
    public double TrailingContinuousGapPoints { get; set; } = 25.0;

    // >>> NEW: 3-Minute MACD (12/26/9) Intraday Strategy
    // (per "3-Minute MACD & Dynamic Trailing SL Strategy" doc). Scoped to Nifty only for
    // now, same as the rest of this bot -- Bank Nifty/Sensex values are listed in the doc
    // but wiring a second/third index (own instrument key, own option chain, own
    // WebSocket subscription) is a bigger multi-index change, not part of this strategy.
    public bool MacdStrategyEnabled { get; set; } = true;

    /// <summary>Nifty 50 lot size for this strategy (same as the other two strategies).</summary>
    public int MacdLotSize { get; set; } = 65;

    /// <summary>First target, in premium points, above entry (informational milestone only -- sends one "Target Hit" alert, does not auto-exit; the shared trailing rule above governs the actual stop-loss).</summary>
    public double MacdFirstTargetPoints { get; set; } = 30.0;

    /// <summary>IST time-of-day at which any open MACD position is force-closed (no overnight carry, same intraday-only philosophy as the breakout strategy).</summary>
    public string MacdForceExitTime { get; set; } = "15:20";

    /// <summary>How many previous TRADING days of 3-min candles to replay at startup so MACD(12,26,9) is already warmed up (Signal line needs ~35 candles).</summary>
    public int MacdSeedLookbackTradingDays { get; set; } = 2;

    // >>> NEW: CPR (Central Pivot Range) end-of-day info alert thresholds.
    // CPR width as a % of close below this -> "Narrow" (trending-day read); above the wide one -> "Wide" (range-bound read).
    public double CprNarrowThresholdPercent { get; set; } = 0.15;
    public double CprWideThresholdPercent { get; set; } = 0.40;
}
