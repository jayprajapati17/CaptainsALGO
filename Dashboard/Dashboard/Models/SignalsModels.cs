using AlgoData.Models;

namespace Dashboard.Models;

public sealed class SignalRow
{
    public int Id { get; set; }
    public StrategyType Strategy { get; set; }
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
}

public sealed class SignalsViewModel
{
    public string? Error { get; set; }

    public string StrategyFilter { get; set; } = "all";   // all | ema | breakout | macd
    public string Outcome { get; set; } = "all";          // all | opened | skipped
    public string Period { get; set; } = "all";           // today | 7d | 30d | all
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalPages { get; set; } = 1;

    public int TotalSignals { get; set; }
    public int FilteredCount { get; set; }
    public int OpenedCount { get; set; }
    public int SkippedCount => TotalSignals - OpenedCount;

    public List<SignalRow> Rows { get; set; } = new();
}