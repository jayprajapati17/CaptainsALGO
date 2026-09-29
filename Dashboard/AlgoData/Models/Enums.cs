namespace AlgoData.Models;

public enum StrategyType
{
    Ema,
    Breakout,
    Macd
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