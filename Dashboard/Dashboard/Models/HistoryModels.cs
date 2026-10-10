using AlgoData.Models;

namespace Dashboard.Models;

public sealed class HistoryRow
{
    public int Id { get; set; }
    public StrategyType Strategy { get; set; }
    public string Direction { get; set; } = string.Empty;
    public PositionLeg? Leg { get; set; }
    public string TradingSymbol { get; set; } = string.Empty;
    public OptionType OptionType { get; set; }
    public decimal EntryPremium { get; set; }
    public decimal? ExitPremium { get; set; }
    public DateTimeOffset EntryTime { get; set; }
    public DateTimeOffset? ExitTime { get; set; }
    public string? ExitReason { get; set; }
    public decimal? StopLossPremium { get; set; }
    public decimal? StopLossSpot { get; set; }
    public decimal? FinalPnlRupees { get; set; }
    public double? FinalPnlPercent { get; set; }

    public TimeSpan? Held => ExitTime.HasValue ? ExitTime.Value - EntryTime : null;
}

public sealed class HistoryViewModel
{
    public string? Error { get; set; }

    // Current filter/sort/paging state -- echoed back so the view can build chip links.
    public string StrategyFilter { get; set; } = "all";   // all | ema | breakout | macd
    public string From { get; set; } = "";                // yyyy-MM-dd or empty
    public string To { get; set; } = "";                  // yyyy-MM-dd or empty
    public string Sort { get; set; } = "time";            // time | pnl_desc | pnl_asc
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalRows { get; set; }
    public decimal TotalPnl { get; set; }
    public int TotalPages { get; set; } = 1;

    public List<HistoryRow> Rows { get; set; } = new();
}