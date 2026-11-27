using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AlgoData.Data;
using AlgoData.Models;
using Dashboard.Models;

namespace Dashboard.Controllers;

/// <summary>
/// Performance: win rate, drawdown, daily P&amp;L and the equity curve, computed from the closed rows of
/// the Positions table. Only a few columns are pulled, then everything is aggregated in memory
/// (a paper bot closes a few trades a day, so the set is small) which keeps the maths in plain C#.
/// Each Positions row counts as one trade (an EMA signal has two legs, so it shows up as two trades).
/// </summary>
public sealed class PerformanceController : Controller
{
    private readonly NiftyBotDbContext _db;
    private readonly ILogger<PerformanceController> _logger;

    public PerformanceController(NiftyBotDbContext db, ILogger<PerformanceController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private sealed record Row(StrategyType Strategy, string Underlying, DateTimeOffset EntryTime,
                              DateTimeOffset ExitTime, string? ExitReason, decimal Pnl);

    public async Task<IActionResult> Index(string? strategy, string? period, CancellationToken ct = default)
    {
        StrategyType? strategyFilter = Enum.TryParse<StrategyType>(strategy, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

        var periodKey = (period ?? "90").ToLowerInvariant();
        if (periodKey is not ("30" or "90" or "all")) periodKey = "90";

        var vm = new PerformanceViewModel
        {
            StrategyFilter = strategyFilter?.ToString().ToLowerInvariant() ?? "all",
            Period = periodKey
        };

        try
        {
            var q = _db.Positions.AsNoTracking()
                .Where(p => p.Status == PositionStatus.Closed && p.ExitTime != null && p.FinalPnlRupees != null);
            if (strategyFilter is { } s) q = q.Where(p => p.Strategy == s);
            q = q.ForUnderlying(Underlyings.Nifty50);   // only Nifty rows are shown
            if (periodKey != "all")
            {
                var from = MarketTime.StartOfDay(MarketTime.Today.AddDays(-(int.Parse(periodKey) - 1)));
                q = q.Where(p => p.ExitTime >= from);
            }

            var rows = (await q
                    .Select(p => new { p.Strategy, p.Underlying, p.EntryTime, ExitTime = p.ExitTime!.Value, p.ExitReason, Pnl = p.FinalPnlRupees!.Value })
                    .ToListAsync(ct))
                .Select(p => new Row(p.Strategy, p.Underlying, p.EntryTime, p.ExitTime, p.ExitReason, p.Pnl))
                .OrderBy(r => r.ExitTime)
                .ToList();

            Fill(vm, rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load performance data.");
            vm.Error = "Could not load closed positions from the database -- check the connection string / SQL Server.";
        }

        return View(vm);
    }

    private static PerfSlice Slice(string label, IEnumerable<Row> rows)
    {
        var sl = new PerfSlice { Label = label };
        foreach (var r in rows)
        {
            sl.Trades++;
            sl.NetPnl += r.Pnl;
            if (r.Pnl > 0) { sl.Wins++; sl.GrossWin += r.Pnl; }
            else if (r.Pnl < 0) { sl.Losses++; sl.GrossLoss += -r.Pnl; }
        }
        return sl;
    }

    private static void Fill(PerformanceViewModel vm, List<Row> rows)
    {
        vm.Overall = Slice("Overall", rows);
        if (rows.Count == 0) return;

        // ---- per trading day (IST) + equity curve + drawdown ----
        vm.Days = rows
            .GroupBy(r => DateOnly.FromDateTime(r.ExitTime.ToIst().DateTime))
            .OrderBy(g => g.Key)
            .Select(g => new PerfDay { Day = g.Key, Pnl = g.Sum(r => r.Pnl), Trades = g.Count() })
            .ToList();

        decimal run = 0, peak = 0;
        DateOnly peakDay = vm.Days[0].Day;
        foreach (var d in vm.Days)
        {
            run += d.Pnl;
            vm.Equity.Add(run);
            if (run > peak) { peak = run; peakDay = d.Day; }
            var dd = peak - run;
            if (dd > vm.MaxDrawdown) { vm.MaxDrawdown = dd; vm.DrawdownFrom = peakDay; vm.DrawdownTo = d.Day; }
        }

        vm.BestDay = vm.Days.MaxBy(d => d.Pnl);
        vm.WorstDay = vm.Days.MinBy(d => d.Pnl);
        vm.WinningDays = vm.Days.Count(d => d.Pnl > 0);
        vm.LosingDays = vm.Days.Count(d => d.Pnl < 0);

        // ---- streaks (trade by trade, in exit order) ----
        int win = 0, loss = 0;
        foreach (var r in rows)
        {
            if (r.Pnl > 0) { win++; loss = 0; }
            else if (r.Pnl < 0) { loss++; win = 0; }
            else { win = 0; loss = 0; }
            vm.LongestWinStreak = Math.Max(vm.LongestWinStreak, win);
            vm.LongestLossStreak = Math.Max(vm.LongestLossStreak, loss);
        }

        vm.AvgHold = TimeSpan.FromSeconds(rows.Average(r => (r.ExitTime - r.EntryTime).TotalSeconds));

        vm.ByStrategy = rows.GroupBy(r => r.Strategy).OrderBy(g => g.Key)
            .Select(g => Slice(Display.Strategy(g.Key), g)).ToList();
        vm.ByExit = rows.GroupBy(r => string.IsNullOrWhiteSpace(r.ExitReason) ? "Unknown" : r.ExitReason!)
            .Select(g => Slice(g.Key, g)).OrderByDescending(x => x.Trades).ToList();
    }
}