using AlgoData.Models;

namespace Dashboard.Models;

/// <summary>Win/loss numbers for one slice of closed trades (all, one strategy, one index, ...).</summary>
public sealed class PerfSlice
{
    public string Label { get; set; } = "";
    public int Trades { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public decimal NetPnl { get; set; }
    public decimal GrossWin { get; set; }
    public decimal GrossLoss { get; set; }   // positive number
    public decimal AvgWin => Wins == 0 ? 0 : GrossWin / Wins;
    public decimal AvgLoss => Losses == 0 ? 0 : GrossLoss / Losses;
    public double WinRate => Trades == 0 ? 0 : Wins * 100.0 / Trades;
    /// <summary>Gross profit / gross loss; null when there are no losing trades.</summary>
    public decimal? ProfitFactor => GrossLoss == 0 ? null : GrossWin / GrossLoss;
    public decimal Expectancy => Trades == 0 ? 0 : NetPnl / Trades;
}

public sealed class PerfDay
{
    public DateOnly Day { get; set; }
    public decimal Pnl { get; set; }
    public int Trades { get; set; }
}

public sealed class PerformanceViewModel
{
    public string? Error { get; set; }
    public string StrategyFilter { get; set; } = "all";
    public string Period { get; set; } = "90";            // 30 | 90 | all

    public PerfSlice Overall { get; set; } = new() { Label = "Overall" };
    public List<PerfDay> Days { get; set; } = new();       // trading days, oldest first
    public List<decimal> Equity { get; set; } = new();     // cumulative P&L after each trading day
    public decimal MaxDrawdown { get; set; }               // positive number (peak-to-trough, rupees)
    public DateOnly? DrawdownFrom { get; set; }
    public DateOnly? DrawdownTo { get; set; }
    public PerfDay? BestDay { get; set; }
    public PerfDay? WorstDay { get; set; }
    public int WinningDays { get; set; }
    public int LosingDays { get; set; }
    public int LongestWinStreak { get; set; }
    public int LongestLossStreak { get; set; }
    public TimeSpan? AvgHold { get; set; }

    public List<PerfSlice> ByStrategy { get; set; } = new();
    public List<PerfSlice> ByExit { get; set; } = new();
}