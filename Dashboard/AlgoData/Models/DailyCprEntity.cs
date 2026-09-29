namespace AlgoData.Models;

public enum CprLabel
{
    TodaysCpr,
    NextDayCpr
}

/// <summary>Maps 1:1 to the `DailyCpr` table in schema-sqlserver.sql.</summary>
public sealed class DailyCprEntity
{
    public int Id { get; set; }

    public DateOnly ForTradingDay { get; set; }
    public CprLabel Label { get; set; }

    public decimal SourceHigh { get; set; }
    public decimal SourceLow { get; set; }
    public decimal SourceClose { get; set; }

    public decimal CPRPivot { get; set; }
    public decimal Tc { get; set; }
    public decimal Bc { get; set; }
    public decimal R1 { get; set; }
    public decimal S1 { get; set; }
    public decimal R2 { get; set; }
    public decimal S2 { get; set; }

    public double WidthPercent { get; set; }
    public string Reading { get; set; } = string.Empty;
    public string BiasNote { get; set; } = string.Empty;

    public DateTimeOffset ComputedAt { get; set; }
}