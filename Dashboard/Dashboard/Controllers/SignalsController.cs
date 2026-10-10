using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Dashboard.Models;
using AlgoData.Data;
using AlgoData.Models;

namespace Dashboard.Controllers;

/// <summary>
/// Signals Log (Task 11): every confirmed signal from all three strategies, whether or not it
/// turned into a trade (skipped ones carry their reason). Filters and paging run in SQL.
/// </summary>
public sealed class SignalsController : Controller
{
    private const int PageSize = 10;

    private readonly NiftyBotDbContext _db;
    private readonly ILogger<SignalsController> _logger;

    public SignalsController(NiftyBotDbContext db, ILogger<SignalsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IActionResult> Index(string? strategy, string? outcome, string? period, int page = 1, CancellationToken ct = default)
    {
        StrategyType? strategyFilter = Enum.TryParse<StrategyType>(strategy, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : null;

        var outcomeKey = (outcome ?? "all").ToLowerInvariant();
        if (outcomeKey is not ("all" or "opened" or "skipped")) outcomeKey = "all";

        var periodKey = (period ?? "all").ToLowerInvariant();
        if (periodKey is not ("today" or "7d" or "30d" or "all")) periodKey = "all";

        var vm = new SignalsViewModel
        {
            StrategyFilter = strategyFilter?.ToString().ToLowerInvariant() ?? "all",
            Outcome = outcomeKey,
            Period = periodKey,
            PageSize = PageSize
        };

        try
        {
            var q = _db.SignalLogs.AsNoTracking().AsQueryable();

            if (strategyFilter is { } s)
                q = q.Where(x => x.Strategy == s);

            if (CutoffFor(periodKey) is { } cutoff)
                q = q.Where(x => x.SignalTime >= cutoff);

            // Outcome counts reflect strategy + period filters, but NOT the outcome chip itself,
            // so the chips always show how the current selection splits.
            vm.TotalSignals = await q.CountAsync(ct);
            vm.OpenedCount = vm.TotalSignals == 0 ? 0 : await q.CountAsync(x => x.PositionOpened, ct);

            if (outcomeKey == "opened") q = q.Where(x => x.PositionOpened);
            else if (outcomeKey == "skipped") q = q.Where(x => !x.PositionOpened);

            var filteredCount = outcomeKey switch
            {
                "opened" => vm.OpenedCount,
                "skipped" => vm.SkippedCount,
                _ => vm.TotalSignals
            };

            vm.FilteredCount = filteredCount;
            vm.TotalPages = Math.Max(1, (int)Math.Ceiling(filteredCount / (double)PageSize));
            vm.Page = Math.Clamp(page, 1, vm.TotalPages);

            vm.Rows = await q
                .OrderByDescending(x => x.SignalTime)
                .ThenByDescending(x => x.Id)
                .Skip((vm.Page - 1) * PageSize)
                .Take(PageSize)
                .Select(x => new SignalRow
                {
                    Id = x.Id,
                    Strategy = x.Strategy,
                    Direction = x.Direction,
                    Confidence = x.Confidence,
                    SignalTime = x.SignalTime,
                    SpotPrice = x.SpotPrice,
                    AdxAtSignal = x.AdxAtSignal,
                    AdxNCandlesAgo = x.AdxNCandlesAgo,
                    BrokenLevel = x.BrokenLevel,
                    IsCatchUp = x.IsCatchUp,
                    PositionOpened = x.PositionOpened,
                    PositionId = x.PositionId,
                    SkipReason = x.SkipReason
                })
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load the signal log for the Signals page.");
            vm.Error = "Could not load signals from the database -- check the connection string / SQL Server.";
        }

        return View(vm);
    }

    private static DateTimeOffset? CutoffFor(string period) => period switch
    {
        "today" => new DateTimeOffset(DateTime.Today),
        "7d" => new DateTimeOffset(DateTime.Today.AddDays(-6)),
        "30d" => new DateTimeOffset(DateTime.Today.AddDays(-29)),
        _ => null
    };
}