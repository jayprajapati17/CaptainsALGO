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
            vm.WorkerError = $"Worker Service ({_workerEndpoints.BaseUrl}) se connect nahi ho pa raha -- open positions load nahi hui. Worker chalu hai?";
        }

        vm.Cpr = await LoadCprAsync(ct);
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

    private async Task<List<OpenPositionDto>> FetchOpenPositionsAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("WorkerApi");
        var positions = await client.GetFromJsonAsync<List<OpenPositionDto>>("/api/positions/open", JsonOptions, ct);
        return positions ?? new List<OpenPositionDto>();
    }

    /// <summary>
    /// Today's CPR from the DailyCpr table. Prefers a row whose ForTradingDay is today
    /// (morning "TodaysCpr", else the previous evening's "NextDayCpr"); otherwise falls back to
    /// the most recent one, flagged as not-for-today. Null if the table is empty or the DB is unreachable.
    /// </summary>
    private async Task<CprStrip?> LoadCprAsync(CancellationToken ct)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);

            var todaysRows = await _db.DailyCprs.AsNoTracking()
                .Where(c => c.ForTradingDay == today)
                .ToListAsync(ct);

            // Label is stored as text in the DB, so pick in memory rather than ordering by it.
            var forToday = todaysRows.FirstOrDefault(c => c.Label == CprLabel.TodaysCpr)
                           ?? todaysRows.FirstOrDefault();

            var row = forToday ?? await _db.DailyCprs.AsNoTracking()
                .Where(c => c.ForTradingDay < today)
                .OrderByDescending(c => c.ForTradingDay)
                .FirstOrDefaultAsync(ct);

            if (row is null)
                return null;

            return new CprStrip
            {
                ForTradingDay = row.ForTradingDay,
                IsForToday = row.ForTradingDay == today,
                Pivot = row.CPRPivot,
                Tc = row.Tc,
                Bc = row.Bc,
                WidthPercent = row.WidthPercent,
                Reading = row.Reading,
                BiasNote = row.BiasNote
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load CPR for the Live page header.");
            return null;
        }
    }
}