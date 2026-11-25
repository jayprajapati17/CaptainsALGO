namespace AlgoData.Models;

public enum StrategyType
{
    Ema,
    Breakout,
    Macd,
    Reversal,
    EmaCrossA,   // EMA 9/21 options strategy, 15-minute candles (Strategy A)
    EmaCrossB    // EMA 9/21 options strategy, 1-hour candles (Strategy B)
}

public enum ConfidenceLevel
{
    High,
    Medium,
    Low
}

public enum OptionType
{
    CE,
    PE
}

public enum PositionStatus
{
    Open,
    Closed
}

/// <summary>Matches Positions.Leg -- null for Breakout (single-leg strategy).</summary>
public enum PositionLeg
{
    CurrentWeekItm,
    NextWeekAtm
}