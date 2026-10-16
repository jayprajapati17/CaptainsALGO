using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Dashboard.Models;
using AlgoData.Data;
using AlgoData.Models;

namespace Dashboard.Controllers;

/// <summary>
/// Live Dashboard (Task 8). Initial load: server-side GET /api/positions/open from the Worker
/// + today's CPR straight from the shared DB. After that, the browser's SignalR client
/// (wwwroot/js/live.js) takes over for real-time updates. "Exit" clicks come here first
/// (same-origin, so no CORS) and are forwarded to the Worker's Command API.
/// </summary>
public sealed class LiveController : Controller
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WorkerEndpoints _workerEndpoints;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NiftyBotDbContext _db;
    private readonly ILogger<LiveController> _logger;

    public LiveController(
        WorkerEndpoints workerEndpoints,
        IHttpClientFactory httpClientFactory,
        NiftyBotDbContext db,
        ILogger<LiveController> logger)
    {
        _workerEndpoints = workerEndpoints;
        _httpClientFactory = httpClientFactory;
        _db = db;
        _logger = logger;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var vm = new LiveIndexViewModel { PositionsHubUrl = _workerEndpoints.PositionsHubUrl };

        try
        {
            vm.Positions = await FetchOpenPositionsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load open positions from the Worker Service at {Url}.", _workerEndpoints.BaseUrl);
            vm.WorkerError = $"Worker Service ({_workerEndpoints.BaseUrl}) is unreachable -- open positions could not be loaded. Is the Worker running?";
        }

        vm.NiftyCpr = await LoadCprAsync("Nifty 50", "nifty", AlgoData.Models.Underlyings.Nifty50, ct);
        vm.BankNiftyCpr = await LoadCprAsync("Bank Nifty", "banknifty", AlgoData.Models.Underlyings.BankNifty, ct);
        vm.TargetPoints = await LoadStrategyNumberAsync("TrailingTargetPoints", 30, ct);
        vm.InitialRiskPoints = await LoadStrategyNumberAsync("TrailingInitialRiskPoints", 15, ct);
        vm.BnTargetPoints = await LoadStrategyNumberAsync("TrailingTargetPoints", 50, ct, "BankNifty");
        vm.BnInitialRiskPoints = await LoadStrategyNumberAsync("TrailingInitialRiskPoints", 30, ct, "BankNifty");
        return View(vm);
    }

    /// <summary>JSON snapshot of open positions -- used by live.js to resync after a SignalR reconnect or a close.</summary>
    [HttpGet]
    public async Task<IActionResult> Open(CancellationToken ct)
    {
        try
        {
            return Json(await FetchOpenPositionsAsync(ct), JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open-positions resync failed.");
            return StatusCode(502, new { message = "Worker Service unreachable." });
        }
    }

    /// <summary>"Exit" button: forwards to the Worker's POST /api/positions/{id}/close.</summary>
    [HttpPost]
    public async Task<IActionResult> Close(int id, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("WorkerApi");
            using var response = await client.PostAsync($"/api/positions/{id}/close", content: null, ct);

            if (response.IsSuccessStatusCode)
                return Json(new { closed = true });

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return NotFound(new { closed = false, message = "Position already closed (or no longer tracked by the Worker)." });

            return StatusCode(502, new { closed = false, message = $"Worker returned {(int)response.StatusCode}." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Close request for position {Id} failed to reach the Worker.", id);
            return StatusCode(502, new { closed = false, message = "Worker Service unreachable -- position NOT closed." });
        }
    }

    /// <summary>A numeric Strategy:* value from the AppSettings table (falls back to the given default).</summary>
    private async Task<double> LoadStrategyNumberAsync(string key, double fallback, CancellationToken ct, string section = "Strategy")
    {
        try
        {
            var raw = await _db.AppSettings.AsNoTracking()
                .Where(a => a.Section == section && a.Key == key)
                .Select(a => a.Value)
                .FirstOrDefaultAsync(ct);
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
                return v;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read Strategy:{Key} from AppSettings; using {Fallback}.", key, fallback);
        }
        return fallback;
    }

    private async Task<List<OpenPositionDto>> FetchOpenPositionsAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("WorkerApi");
        var positions = await client.GetFromJsonAsync<List<OpenPositionDto>>("/api/positions/open", JsonOptions, ct)
                        ?? new List<OpenPositionDto>();

        return positions;
    }

    /// <summary>
    /// One index's CPR from the DailyCpr table. TODAY: the row for today (morning "TodaysCpr", else the previous evening's "NextDayCpr"),
    /// otherwise the most recent earlier one (flagged IsForToday = false). TOMORROW: the latest "NextDayCpr" for a day after today
    /// (written after the close). Either part is null when there is nothing yet or the DB is unreachable.
    /// </summary>
    private async Task<CprPair> LoadCprAsync(string name, string key, string underlying, CancellationToken ct)
    {
        try
        {
            var today = AlgoData.Models.MarketTime.Today;
            var rows = await _db.DailyCprs.AsNoTracking()
                .Where(c => c.Underlying == underlying && c.ForTradingDay >= today.AddDays(-10))
                .ToListAsync(ct);

            // Label is stored as text in the DB, so pick in memory rather than ordering by it.
            var todayRows = rows.Where(c => c.ForTradingDay == today).ToList();
            var todayRow = todayRows.FirstOrDefault(c => c.Label == CprLabel.TodaysCpr)
                           ?? todayRows.FirstOrDefault()
                           ?? rows.Where(c => c.ForTradingDay < today).OrderByDescending(c => c.ForTradingDay).FirstOrDefault();

            var tomorrowRow = rows.Where(c => c.Label == CprLabel.NextDayCpr && c.ForTradingDay > today)
                .OrderByDescending(c => c.ForTradingDay).FirstOrDefault();

            return new CprPair { Name = name, Key = key, Today = ToStrip(todayRow, today), Tomorrow = ToStrip(tomorrowRow, today) };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load {Index} CPR for the Live page.", name);
            return new CprPair { Name = name, Key = key };
        }
    }

    private static CprStrip? ToStrip(DailyCprEntity? row, DateOnly today) => row is null ? null : new CprStrip
    {
        ForTradingDay = row.ForTradingDay,
        IsForToday = row.ForTradingDay == today,
        Pivot = row.CPRPivot,
        Tc = row.Tc,
        Bc = row.Bc,
        R1 = row.R1,
        S1 = row.S1,
        R2 = row.R2,
        S2 = row.S2,
        WidthPercent = row.WidthPercent,
        Reading = row.Reading,
        BiasNote = row.BiasNote
    };
}