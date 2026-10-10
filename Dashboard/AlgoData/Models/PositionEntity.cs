namespace AlgoData.Models;

/// <summary>Maps 1:1 to the `Positions` table in schema-sqlserver.sql.</summary>
public sealed class PositionEntity
{
    public int Id { get; set; }

    public StrategyType Strategy { get; set; }
    public string Direction { get; set; } = string.Empty; // "GoldenCross" | "DeathCross" | "Up" | "Down"
    public PositionLeg? Leg { get; set; }                  // null for Breakout

    // >>> NEW (multi-instrument): which underlying (InstrumentDefinition.Key, e.g.
    // "NIFTY50", "BANKNIFTY", "RELIANCE") this position belongs to -- NOT the option
    // contract's own Upstox key, that's still InstrumentKey below. Defaults to
    // "NIFTY50" for rows written before multi-instrument support existed.
    public string Underlying { get; set; } = "NIFTY50";

    public string InstrumentKey { get; set; } = string.Empty;
    public string TradingSymbol { get; set; } = string.Empty; // e.g. "NIFTY50 24350CE"
    public int Strike { get; set; }
    public OptionType OptionType { get; set; }
    public DateOnly Expiry { get; set; }
    public int LotSize { get; set; }

    public decimal SpotAtEntry { get; set; }
    public decimal EntryPremium { get; set; }
    public DateTimeOffset EntryTime { get; set; }

    public ConfidenceLevel? Confidence { get; set; } // EMA only
    public double? AdxAtEntry { get; set; }
    public double? AdxNCandlesAgo { get; set; }
    public decimal? BrokenLevel { get; set; } // Breakout only

    public PositionStatus Status { get; set; } = PositionStatus.Open;
    public decimal LastKnownPremium { get; set; }
    public DateTimeOffset LastUpdateTime { get; set; }

    /// <summary>Current (trailing) stop-loss as an OPTION PREMIUM -- set at entry, updated whenever it moves, and left at its final value once the position closes. Null for Reversal (spot-based SL).</summary>
    public decimal? StopLossPremium { get; set; }

    /// <summary>Current stop-loss as a NIFTY SPOT level -- Reversal strategy only (null for the premium-based strategies).</summary>
    public decimal? StopLossSpot { get; set; }

    public decimal? ExitPremium { get; set; }
    public DateTimeOffset? ExitTime { get; set; }
    public string? ExitReason { get; set; }
    public decimal? FinalPnlRupees { get; set; }
    public double? FinalPnlPercent { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // ---- Convenience (not mapped columns, computed for display) ----
    public decimal LivePnlRupees => (LastKnownPremium - EntryPremium) * LotSize;
    public double LivePnlPercent => EntryPremium == 0 ? 0 : (double)((LastKnownPremium - EntryPremium) / EntryPremium) * 100.0;
}