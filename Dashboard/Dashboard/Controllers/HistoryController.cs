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
    private const int PageSize = 10;

    private readonly NiftyBotDbContext _db;
    private readonly ILogger<HistoryController> _logger;

    public HistoryController(NiftyBotDbContext db, ILogger<HistoryController> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IActionResult> Index(string? strategy, string? from, string? to, string? sort, int page = 1, CancellationToken ct = default)
    {
        StrategyType? strategyFilter = Enum.TryParse<StrategyType>(strategy, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : null;

        DateOnly? fromDate = DateOnly.TryParse(from, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : null;
        DateOnly? toDate = DateOnly.TryParse(to, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : null;
        if (fromDate is { } ff && toDate is { } tt && ff > tt) (fromDate, toDate) = (toDate, fromDate);

        var sortKey = (sort ?? "time").ToLowerInvariant();
        if (sortKey is not ("time" or "pnl_desc" or "pnl_asc")) sortKey = "time";

        var vm = new HistoryViewModel
        {
            StrategyFilter = strategyFilter?.ToString().ToLowerInvariant() ?? "all",
            From = fromDate?.ToString("yyyy-MM-dd") ?? "",
            To = toDate?.ToString("yyyy-MM-dd") ?? "",
            Sort = sortKey,
            PageSize = PageSize
        };

        try
        {
            var q = _db.Positions.AsNoTracking().Where(p => p.Status == PositionStatus.Closed);

            if (strategyFilter is { } s)
                q = q.Where(p => p.Strategy == s);

            if (fromDate is { } fd)
            {
                var start = new DateTimeOffset(fd.ToDateTime(TimeOnly.MinValue));
                q = q.Where(p => p.ExitTime >= start);
            }
            if (toDate is { } td)
            {
                var endExclusive = new DateTimeOffset(td.AddDays(1).ToDateTime(TimeOnly.MinValue));
                q = q.Where(p => p.ExitTime < endExclusive);
            }

            // ---- paging ----
            var totalRows = await q.CountAsync(ct);
            vm.TotalRows = totalRows;
            if (totalRows > 0)
                vm.TotalPnl = await q.SumAsync(p => p.FinalPnlRupees ?? 0m, ct);
            vm.TotalPages = Math.Max(1, (int)Math.Ceiling(totalRows / (double)PageSize));
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
                    StopLossPremium = p.StopLossPremium,
                    StopLossSpot = p.StopLossSpot,
                    FinalPnlRupees = p.FinalPnlRupees,
                    FinalPnlPercent = p.FinalPnlPercent
                })
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load closed positions for the History page.");
            vm.Error = "Could not load closed positions from the database -- check the connection string / SQL Server.";
        }

        return View(vm);
    }

}
