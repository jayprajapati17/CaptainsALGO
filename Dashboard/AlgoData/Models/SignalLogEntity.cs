namespace AlgoData.Models;

/// <summary>Maps 1:1 to the `SignalLog` table in schema-sqlserver.sql.</summary>
public sealed class SignalLogEntity
{
    public int Id { get; set; }

    public StrategyType Strategy { get; set; }

    // >>> NEW (multi-instrument): which underlying (InstrumentDefinition.Key) this
    // signal belongs to. Defaults to "NIFTY50" for rows written before multi-instrument support existed.
    public string Underlying { get; set; } = "NIFTY50";

    public string Direction { get; set; } = string.Empty;
    public ConfidenceLevel? Confidence { get; set; }
    public DateTimeOffset SignalTime { get; set; }
    public decimal SpotPrice { get; set; }
    public double? AdxAtSignal { get; set; }
    public double? AdxNCandlesAgo { get; set; }
    public decimal? BrokenLevel { get; set; }
    public bool IsCatchUp { get; set; }

    public bool PositionOpened { get; set; }
    public int? PositionId { get; set; }
    public string? SkipReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}