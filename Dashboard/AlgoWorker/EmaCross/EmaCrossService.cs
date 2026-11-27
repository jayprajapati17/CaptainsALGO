using AlgoData.Models;
using AlgoWorker.EmaCross.Core;
using AlgoWorker.Configuration;
using AlgoWorker.Hubs;
using AlgoWorker.Models;
using AlgoWorker.Services;
using AlgoWorker.Services.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AlgoWorker.EmaCross;

/// <summary>
/// Hosts EMA 9/21 Strategy A (15-min) and B (1-hour). Candles come from Upstox's own 15-minute candle data (polled shortly after
/// each 15-minute boundary, as the guide's section 9.1 asks: a candle counts only once its end time has passed AND the broker's
/// data confirms it); the 1-hour candles are built from four 15-minute candles on the 09:15 grid (09:15, 10:15 ... 15:15, the
/// last one being only 15 minutes long). It does not own the WebSocket: it listens to the shared feed's ticks for the Nifty
/// index stop-loss and for its open option contract.
/// </summary>
public sealed class EmaCrossService : BackgroundService, IMarketFeedConsumer
{
    private static readonly TimeSpan SessionOpen = new(9, 15, 0);
    private static readonly TimeSpan LastCandleStart = new(15, 15, 0);

    private readonly UpstoxRestClient _rest;
    private readonly UpstoxWebSocketClient _ws;
    private readonly MarketHoursService _market;
    private readonly TelegramAlertService _telegram;
    private readonly IPositionRepository _positions;
    private readonly EmaCrossOptions _opt;
    private readonly UpstoxOptions _upstox;
    private readonly List<EmaCrossRunner> _runners = new();
    private readonly EmaCrossRunner? _runnerA, _runnerB;
    private readonly ILogger<EmaCrossService> _logger;

    private readonly List<Bar> _hourBuffer = new();
    private bool _seeded;
    private DateOnly _readyDay;
    private DateTime? _expectedStart;      // start time of the next 15-min candle we are waiting for (IST wall clock)
    private bool _gapWarned;

    /// <summary>The Nifty index is already subscribed by the Nifty Worker; open option contracts are added when a trade opens.</summary>
    public IReadOnlyCollection<string> FeedInstrumentKeys => Array.Empty<string>();

    public EmaCrossService(UpstoxRestClient rest, UpstoxWebSocketClient ws, MarketHoursService market, TelegramAlertService telegram,
        EmaCrossOptionSelector selector, IPositionRepository positions, ISignalLogRepository signalLog, IHubContext<PositionHub> hub,
        IClock clock, IOptions<EmaCrossOptions> options, IOptions<UpstoxOptions> upstox, ILoggerFactory loggerFactory, ILogger<EmaCrossService> logger)
    {
        _rest = rest; _ws = ws; _market = market; _telegram = telegram; _positions = positions;
        _opt = options.Value; _upstox = upstox.Value; _logger = logger;

        if (_opt.StrategyAEnabled)
            _runners.Add(_runnerA = new EmaCrossRunner(EmaCrossVariant.A(_opt), _opt, _upstox, selector, rest, ws, telegram, positions, signalLog, hub, clock, loggerFactory.CreateLogger("AlgoWorker.EmaCross.A")));
        if (_opt.StrategyBEnabled)
            _runners.Add(_runnerB = new EmaCrossRunner(EmaCrossVariant.B(_opt), _opt, _upstox, selector, rest, ws, telegram, positions, signalLog, hub, clock, loggerFactory.CreateLogger("AlgoWorker.EmaCross.B")));

        _ws.TickReceived += OnTickReceived;
    }

    // ------------------------------------------------------------------ Dashboard / API helpers

    public async Task<bool> ForceCloseAsync(int id, CancellationToken ct)
    {
        foreach (var r in _runners)
            if (await r.ForceCloseAsync(id, ct)) return true;
        return false;
    }

    public IEnumerable<PositionUpdateDto> GetOpenPositionsSnapshot() => _runners.SelectMany(r => r.GetOpenPositionsSnapshot());

    // ------------------------------------------------------------------ main loop

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();   // let startup (settings seeding, orphan clean-up) finish first
        if (!_opt.Enabled || _runners.Count == 0)
        {
            _logger.LogWarning("EmaCross: disabled (EmaCross:Enabled / strategy switches) -- EMA 9/21 pipeline idle.");
            return;
        }

        _logger.LogInformation("EMA 9/21 pipeline starting ({Count} strategy(ies)); paper trading only.", _runners.Count);

        // These strategies are held overnight: take over any trade that was still open when the Worker last stopped.
        await RestoreOpenLegsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CycleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (UnauthorizedTokenException)
            {
                _logger.LogWarning("EMA 9/21: Upstox token missing/expired -- waiting for a fresh one.");
                await DelaySafe(TimeSpan.FromMinutes(2), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EMA 9/21: unhandled error in the loop -- will retry shortly.");
                await DelaySafe(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task CycleAsync(CancellationToken ct)
    {
        var nowIst = DateTimeOffset.UtcNow.ToIst();
        var today = DateOnly.FromDateTime(nowIst.DateTime);
        var tod = TimeOnly.FromDateTime(nowIst.DateTime);

        bool weekday = nowIst.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        bool inWindow = weekday && tod >= TimeOfDay.Parse(_opt.SessionStartTime) && tod <= TimeOfDay.Parse(_opt.SessionEndTime).AddMinutes(10);
        if (!inWindow) { await DelaySafe(TimeSpan.FromSeconds(30), ct); return; }

        if (_readyDay != today)
        {
            if (!await _market.IsMarketOpenAsync(ct)) { await DelaySafe(TimeSpan.FromMinutes(1), ct); return; }   // holiday / pre-open

            if (!_seeded)
            {
                await SeedAsync(ct);
                _seeded = true;
            }

            // First candle we wait for today: the one after the last candle fed (yesterday's 15:15 -> today's 09:15).
            if (_expectedStart is null || _expectedStart.Value.Date < today.ToDateTime(TimeOnly.MinValue))
                _expectedStart = today.ToDateTime(TimeOnly.MinValue) + SessionOpen;
            _gapWarned = false;
            _readyDay = today;

            foreach (var r in _runners)
                await r.OnSessionOpenAsync(today, ct);
        }

        if (_expectedStart is DateTime expected && expected.Date == today.ToDateTime(TimeOnly.MinValue))
        {
            var due = expected.AddMinutes(15).AddSeconds(3);
            if (nowIst.DateTime >= due)
            {
                var fed = await FetchAndFeedAsync(expected, ct);
                if (!fed && nowIst.DateTime > expected.AddMinutes(15).AddSeconds(90) && !_gapWarned)
                {
                    _gapWarned = true;
                    await _telegram.SendWarningAsync($"EMA 9/21: Upstox has not returned the {expected:HH:mm} 15-min candle after 90 s -- still retrying. " +
                                                     "Signals stay paused until it arrives.", ct);
                }
            }
        }

        await DelaySafe(TimeSpan.FromSeconds(3), ct);
    }

    private async Task RestoreOpenLegsAsync(CancellationToken ct)
    {
        var rows = await _positions.GetOpenPositionsAsync(new[] { StrategyType.EmaCrossA, StrategyType.EmaCrossB }, Underlyings.Nifty50, ct);
        foreach (var runner in _runners)
            foreach (var row in rows.Where(r => r.Strategy == runner.Strategy))
                await runner.RestoreAsync(row, ct);
    }

    // ------------------------------------------------------------------ candles

    private async Task<bool> FetchAndFeedAsync(DateTime expectedStart, CancellationToken ct)
    {
        var candles = await _rest.GetIntradayCandlesAsync(_upstox.NiftyInstrumentKey, 15, ct);
        var nowIst = DateTimeOffset.UtcNow.ToIst().DateTime;
        var closed = candles
            .Select(c => (Time: c.OpenTime.ToIst().DateTime, Candle: c))
            .Where(x => x.Time >= expectedStart && x.Time.AddMinutes(15) <= nowIst)
            .OrderBy(x => x.Time)
            .ToList();
        if (closed.Count == 0 || closed[0].Time != expectedStart) return false;

        foreach (var (time, c) in closed)
        {
            if (_expectedStart is { } e && time != e) break;       // a hole: stop and retry
            await FeedAsync(ToBar(time, c), seeding: false, ct);
            _gapWarned = false;
        }
        return true;
    }

    /// <summary>One closed 15-min candle to Strategy A and, when it completes an hour bucket, the 1-hour candle to Strategy B.</summary>
    private async Task FeedAsync(Bar bar15, bool seeding, CancellationToken ct)
    {
        bool lastOfDay = bar15.Time.TimeOfDay == LastCandleStart;
        _expectedStart = lastOfDay ? NextSessionOpen(bar15.Time.Date) : bar15.Time.AddMinutes(15);

        if (_runnerA != null) await _runnerA.OnCandleAsync(bar15, seeding, lastOfDay, ct);

        if (_runnerB == null) return;
        if (_hourBuffer.Count > 0 && (_hourBuffer[0].Time.Date != bar15.Time.Date || HourBucket(_hourBuffer[0]) != HourBucket(bar15)))
        {
            _logger.LogWarning("EMA 9/21: dropping an incomplete 1-hour bucket starting {Time:HH:mm} (a 15-min candle was missing).", _hourBuffer[0].Time);
            _hourBuffer.Clear();
        }
        _hourBuffer.Add(bar15);

        double minutesFromOpen = (bar15.Time.TimeOfDay - SessionOpen).TotalMinutes;
        bool bucketDone = minutesFromOpen % 60 == 45 || lastOfDay;
        if (!bucketDone) return;

        var hour = new Bar(_hourBuffer[0].Time, _hourBuffer[0].Open, _hourBuffer.Max(b => b.High), _hourBuffer.Min(b => b.Low), _hourBuffer[^1].Close);
        _hourBuffer.Clear();
        await _runnerB.OnCandleAsync(hour, seeding, lastOfDay, ct);
    }

    private static int HourBucket(Bar b) => (int)((b.Time.TimeOfDay - SessionOpen).TotalMinutes / 60);

    private static DateTime NextSessionOpen(DateTime day)
    {
        var d = day.AddDays(1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
        return d + SessionOpen;
    }

    private static Bar ToBar(DateTime istTime, Candle c) => new(istTime, (double)c.Open, (double)c.High, (double)c.Low, (double)c.Close);

    // ------------------------------------------------------------------ history (warm-up)

    /// <summary>
    /// Replays enough closed 15-min candles that both strategies have at least HistoryCandles of their own timeframe (500 by default;
    /// the 1-hour strategy needs about four times as many 15-min candles), so the EMAs / ADX / CPR match the backtest (section 5.1).
    /// Upstox serves at most about a month of 15-min candles per request, so the history is fetched in 27-day windows.
    /// </summary>
    private async Task SeedAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToIst().DateTime);
        int need = _runnerB != null ? _opt.HistoryCandles * 4 : _opt.HistoryCandles;

        var all = new Dictionary<DateTime, Candle>();
        var to = today.AddDays(-1);
        for (int window = 0; window < 10 && all.Count < need; window++)
        {
            var from = to.AddDays(-26);
            var chunk = await _rest.GetHistoricalCandlesAsync(_upstox.NiftyInstrumentKey, from, to, 15, ct);
            foreach (var c in chunk) all[c.OpenTime.ToIst().DateTime] = c;
            to = from.AddDays(-1);
        }

        var nowIst = DateTimeOffset.UtcNow.ToIst().DateTime;
        try
        {
            foreach (var c in await _rest.GetIntradayCandlesAsync(_upstox.NiftyInstrumentKey, 15, ct))
            {
                var t = c.OpenTime.ToIst().DateTime;
                if (t.AddMinutes(15) <= nowIst) all[t] = c;     // closed candles of today only
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnauthorizedTokenException)
        {
            _logger.LogWarning(ex, "EMA 9/21: could not fetch today's intraday candles for the warm-up.");
        }

        _logger.LogInformation("EMA 9/21: replaying {Count} closed 15-min candles to warm up the EMAs.", all.Count);
        foreach (var (time, c) in all.OrderBy(kv => kv.Key))
            await FeedAsync(ToBar(time, c), seeding: true, ct);

        foreach (var r in _runners) r.FinishSeeding();
        await _telegram.SendStatusAsync($"EMA 9/21 strategies ready ({string.Join(" + ", _runners.Select(r => r.Name))}): warmed up on {all.Count} 15-min candles. " +
                                        "Only fresh crossovers trade; paper mode.", ct);
    }

    // ------------------------------------------------------------------ ticks (shared feed)

    private async void OnTickReceived(MarketTick tick)
    {
        try
        {
            if (!_seeded) return;
            if (tick.InstrumentKey == _upstox.NiftyInstrumentKey)
                foreach (var r in _runners) await r.OnIndexTickAsync(tick.LastTradedPrice, CancellationToken.None);
            else
                foreach (var r in _runners) await r.OnOptionTickAsync(tick, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EMA 9/21: error handling tick for {Instrument}", tick.InstrumentKey);
        }
    }

    private static async Task DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }
}
