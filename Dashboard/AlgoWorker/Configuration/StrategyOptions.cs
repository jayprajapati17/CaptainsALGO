namespace AlgoWorker.Configuration;

/// <summary>
/// All tunable knobs for the EMA21/50 crossover strategy. Backed by the
/// backtest documented in Nifty_EMA_Alert_Bot_Design.md, Section 3.5 / 3.5.1.
/// </summary>
public sealed class StrategyOptions
{
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

    // >>> NEW: Breakout strategy stop-loss / trailing stop-loss (points = option premium rupees).

    /// <summary>Fixed stop-loss, in premium points, below entry -- active from the moment the position opens.</summary>
    public double BreakoutStopLossPoints { get; set; } = 10.0;

    /// <summary>Once the premium has moved this many points above entry (favorably), the stop switches to trailing mode.</summary>
    public double BreakoutTrailingTriggerPoints { get; set; } = 10.0;

    /// <summary>Once trailing, the stop is held this many points behind the highest premium seen since entry (ratchets up only).</summary>
    public double BreakoutTrailingStepPoints { get; set; } = 3.0;

    // >>> NEW: same fixed-SL + trailing-SL mechanism as the breakout strategy,
    // applied per-leg to the EMA crossover strategy's virtual positions
    // (current-week ITM leg and next-week ATM leg each get their own SL).
    // This is IN ADDITION TO the existing ADX-flat/decline and opposite-crossover
    // exits -- whichever exit condition triggers first wins.

    /// <summary>Fixed stop-loss, in premium points, below entry -- active from the moment each leg opens.</summary>
    public double EmaStopLossPoints { get; set; } = 10.0;

    /// <summary>Once a leg's premium has moved this many points above its entry, its stop switches to trailing mode.</summary>
    public double EmaTrailingTriggerPoints { get; set; } = 10.0;

    /// <summary>Once trailing, a leg's stop is held this many points behind its highest premium since entry.</summary>
    public double EmaTrailingStepPoints { get; set; } = 3.0;

    // >>> NEW: 3-Minute MACD (12/26/9) Intraday Strategy with Dynamic Step-Trailing SL
    // (per "3-Minute MACD & Dynamic Trailing SL Strategy" doc). Scoped to Nifty only for
    // now, same as the rest of this bot -- Bank Nifty/Sensex values are listed in the doc
    // but wiring a second/third index (own instrument key, own option chain, own
    // WebSocket subscription) is a bigger multi-index change, not part of this strategy.
    public bool MacdStrategyEnabled { get; set; } = true;

    /// <summary>Nifty 50 lot size for this strategy (same as the other two strategies).</summary>
    public int MacdLotSize { get; set; } = 65;

    /// <summary>Hard initial stop-loss, in premium points, below entry (Nifty row of the doc's table).</summary>
    public double MacdInitialStopLossPoints { get; set; } = 10.0;

    /// <summary>First target, in premium points, above entry (1:3 risk-reward per the doc; informational milestone only, does not auto-exit).</summary>
    public double MacdFirstTargetPoints { get; set; } = 30.0;

    /// <summary>Premium move (points) that counts as one trailing "step" (doc: every +10 points).</summary>
    public double MacdTrailStepTriggerPoints { get; set; } = 10.0;

    /// <summary>How much the stop-loss ratchets up per step (doc: 5-point blocks), applied for every MacdTrailStepTriggerPoints of favorable move -- continues indefinitely past the first target.</summary>
    public double MacdTrailStepSizePoints { get; set; } = 5.0;

    /// <summary>IST time-of-day at which any open MACD position is force-closed (no overnight carry, same intraday-only philosophy as the breakout strategy).</summary>
    public string MacdForceExitTime { get; set; } = "15:20";

    /// <summary>How many previous TRADING days of 3-min candles to replay at startup so MACD(12,26,9) is already warmed up (Signal line needs ~35 candles).</summary>
    public int MacdSeedLookbackTradingDays { get; set; } = 2;

    // >>> NEW: CPR (Central Pivot Range) end-of-day info alert thresholds.
    // CPR width as a % of close below this -> "Narrow" (trending-day read); above the wide one -> "Wide" (range-bound read).
    public double CprNarrowThresholdPercent { get; set; } = 0.15;
    public double CprWideThresholdPercent { get; set; } = 0.40;
}
