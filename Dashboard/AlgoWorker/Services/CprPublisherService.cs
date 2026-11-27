using AlgoData.Models;
using AlgoWorker.Configuration;
using AlgoWorker.Models;
using AlgoWorker.Services.Persistence;
using Microsoft.Extensions.Options;

namespace AlgoWorker.Services;

/// <summary>
/// Keeps the DailyCpr table filled for the Nifty index, which the Dashboard's Live page reads:
///   * "TodaysCpr"  -- computed in the morning from the PREVIOUS session's High/Low/Close  (today's expected range).
///   * "NextDayCpr" -- computed after the close (15:35 IST) from TODAY's High/Low/Close, for the next weekday (tomorrow's expected range).
/// Pure information -- no strategy uses it. Every step is retried every minute until it succeeds (e.g. the Upstox token is not
/// there yet), and a failure here can never affect trading.
/// </summary>
public sealed class CprPublisherService : BackgroundService
{
    private static readonly TimeSpan MorningFrom = new(8, 30, 0);
    private static readonly TimeSpan EveningFrom = new(15, 35, 0);

    private readonly UpstoxRestClient _rest;
    private readonly CprAnalysisService _cpr;
    private readonly ICprRepository _repo;
    private readonly UpstoxOptions _upstox;
    private readonly ILogger<CprPublisherService> _logger;

    private DateOnly _morningDoneFor;
    private DateOnly _eveningDoneFor;
    private DateTimeOffset _lastWarning = DateTimeOffset.MinValue;

    public CprPublisherService(UpstoxRestClient rest, CprAnalysisService cpr, ICprRepository repo,
        IOptions<UpstoxOptions> upstox, ILogger<CprPublisherService> logger)
    {
        _rest = rest; _cpr = cpr; _repo = repo; _upstox = upstox.Value; _logger = logger;
    }

    private IEnumerable<(string Underlying, string Key, string Name)> Indices()
    {
        yield return (Underlyings.Nifty50, _upstox.NiftyInstrumentKey, "Nifty");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();   // let the host finish starting (settings seeded, schema migrated) first
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (DateTimeOffset.UtcNow - _lastWarning > TimeSpan.FromMinutes(10))
                {
                    _lastWarning = DateTimeOffset.UtcNow;
                    _logger.LogWarning(ex, "CPR update failed -- will retry in a minute.");
                }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var nowIst = MarketTime.ToIst(DateTimeOffset.UtcNow);
        var today = DateOnly.FromDateTime(nowIst.Date);
        if (nowIst.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;

        if (_morningDoneFor != today && nowIst.TimeOfDay >= MorningFrom)
        {
            foreach (var (underlying, key, name) in Indices())
                await PublishTodayAsync(underlying, key, name, today, ct);
            _morningDoneFor = today;
        }

        if (_eveningDoneFor != today && nowIst.TimeOfDay >= EveningFrom)
        {
            foreach (var (underlying, key, name) in Indices())
                await PublishNextDayAsync(underlying, key, name, today, ct);
            _eveningDoneFor = today;
        }
    }

    /// <summary>Today's CPR from the previous session's OHLC.</summary>
    private async Task PublishTodayAsync(string underlying, string key, string name, DateOnly today, CancellationToken ct)
    {
        var prev = await _rest.GetPreviousTradingDayOhlcAsync(key, ct);
        if (prev is not { } p)
        {
            _logger.LogWarning("{Index}: previous trading day's OHLC not available -- today's CPR not computed.", name);
            return;
        }
        await Save(underlying, CprLabel.TodaysCpr, _cpr.Compute(p.High, p.Low, p.Close, today), ct);
        _logger.LogInformation("{Index}: today's CPR saved (from {Day}'s OHLC).", name, p.TradingDay);
    }

    /// <summary>Tomorrow's CPR from today's OHLC (built from today's 5-min candles, so it works right after the close).</summary>
    private async Task PublishNextDayAsync(string underlying, string key, string name, DateOnly today, CancellationToken ct)
    {
        var candles = (await _rest.GetIntraday5MinCandlesAsync(key, ct))
            .Where(c => DateOnly.FromDateTime(c.OpenTime.Date) == today)
            .OrderBy(c => c.OpenTime).ToList();
        if (candles.Count == 0)
        {
            _logger.LogInformation("{Index}: no candles today (holiday?) -- tomorrow's CPR not computed.", name);
            return;
        }

        var levels = _cpr.Compute(candles.Max(c => c.High), candles.Min(c => c.Low), candles[^1].Close, CprAnalysisService.NextWeekday(today));
        await Save(underlying, CprLabel.NextDayCpr, levels, ct);
        _logger.LogInformation("{Index}: tomorrow's ({Day}) CPR saved.", name, levels.ForTradingDay);
    }

    private Task Save(string underlying, CprLabel label, CprLevels l, CancellationToken ct) =>
        _repo.SaveAsync(new DailyCprEntity
        {
            Underlying = underlying,
            ForTradingDay = l.ForTradingDay,
            Label = label,
            SourceHigh = l.SourceHigh,
            SourceLow = l.SourceLow,
            SourceClose = l.SourceClose,
            CPRPivot = l.Pivot,
            Tc = l.Tc,
            Bc = l.Bc,
            R1 = l.R1,
            S1 = l.S1,
            R2 = l.R2,
            S2 = l.S2,
            WidthPercent = l.WidthPercent,
            Reading = l.Reading,
            BiasNote = l.BiasNote,
            ComputedAt = DateTimeOffset.UtcNow
        }, ct);
}