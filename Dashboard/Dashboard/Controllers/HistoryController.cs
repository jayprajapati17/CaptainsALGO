using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Dashboard.Models;
using AlgoData.Data;
using AlgoData.Models;

namespace Dashboard.Controllers;

/// <summary>
/// History (Task 10): closed positions straight from the shared DB. Filters (strategy, period),
/// sort and paging all run in SQL; the summary cards are aggregated over the WHOLE filtered
/// set (not just the visible page). Uses LINQ over Positions rather than the vw_ClosedPositions /
/// vw_HistorySummary views, because those views are all-time only and can't honour the filters.
/// </summary>
public sealed class HistoryController : Controller
{
    private const int PageSize = 50;

    private readonly NiftyBotDbContext _db;
    private readonly ILogger<HistoryController> _logger;

    public HistoryController(NiftyBotDbContext db, ILogger<HistoryController> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IActionResult> Index(string? strategy, string? period, string? sort, int page = 1, CancellationToken ct = default)
    {
        StrategyType? strategyFilter = Enum.TryParse<StrategyType>(strategy, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : null;

        var periodKey = (period ?? "all").ToLowerInvariant();
        if (periodKey is not ("today" or "7d" or "30d" or "all")) periodKey = "all";

        var sortKey = (sort ?? "time").ToLowerInvariant();
        if (sortKey is not ("time" or "pnl_desc" or "pnl_asc")) sortKey = "time";

        var vm = new HistoryViewModel
        {
            StrategyFilter = strategyFilter?.ToString().ToLowerInvariant() ?? "all",
            Period = periodKey,
            Sort = sortKey,
            PageSize = PageSize
        };

        try
        {
            var q = _db.Positions.AsNoTracking().Where(p => p.Status == PositionStatus.Closed);

            if (strategyFilter is { } s)
                q = q.Where(p => p.Strategy == s);

            if (CutoffFor(periodKey) is { } cutoff)
                q = q.Where(p => p.ExitTime >= cutoff);

            // ---- summary over the whole filtered set ----
            vm.Summary.TotalTrades = await q.CountAsync(ct);
            if (vm.Summary.TotalTrades > 0)
            {
                vm.Summary.Wins = await q.CountAsync(p => p.FinalPnlRupees > 0, ct);
                vm.Summary.TotalPnl = await q.SumAsync(p => p.FinalPnlRupees ?? 0m, ct);
                vm.Summary.Best = await q.MaxAsync(p => p.FinalPnlRupees, ct);
                vm.Summary.Worst = await q.MinAsync(p => p.FinalPnlRupees, ct);
            }

            // ---- paging ----
            vm.TotalPages = Math.Max(1, (int)Math.Ceiling(vm.Summary.TotalTrades / (double)PageSize));
            vm.Page = Math.Clamp(page, 1, vm.TotalPages);

            var ordered = sortKey switch
            {
                "pnl_desc" => q.OrderByDescending(p => p.FinalPnlRupees).ThenByDescending(p => p.ExitTime),
                "pnl_asc" => q.OrderBy(p => p.FinalPnlRupees).ThenByDescending(p => p.ExitTime),
                _ => q.OrderByDescending(p => p.ExitTime)
            };

            vm.Rows = await ordered
                .Skip((vm.Page - 1) * PageSize)
                .Take(PageSize)
                .Select(p => new Models.HistoryRow
                {
                    Id = p.Id,
                    Strategy = p.Strategy,
                    Direction = p.Direction,
                    Leg = p.Leg,
                    TradingSymbol = p.TradingSymbol,
                    OptionType = p.OptionType,
                    EntryPremium = p.EntryPremium,
                    ExitPremium = p.ExitPremium,
                    EntryTime = p.EntryTime,
                    ExitTime = p.ExitTime,
                    ExitReason = p.ExitReason,
                    FinalPnlRupees = p.FinalPnlRupees,
                    FinalPnlPercent = p.FinalPnlPercent
                })
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load closed positions for the History page.");
            vm.Error = "Database se closed positions load nahi ho payi -- connection string / SQL Server check karo.";
        }

        return View(vm);
    }

    /// <summary>Start of the selected period in local time, or null for "all time".</summary>
    private static DateTimeOffset? CutoffFor(string period) => period switch
    {
        "today" => new DateTimeOffset(DateTime.Today),
        "7d" => new DateTimeOffset(DateTime.Today.AddDays(-6)),
        "30d" => new DateTimeOffset(DateTime.Today.AddDays(-29)),
        _ => null
    };
}