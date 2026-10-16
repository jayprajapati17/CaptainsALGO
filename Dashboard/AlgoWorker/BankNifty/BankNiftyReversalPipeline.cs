using AlgoData.Models;
using AlgoWorker.Configuration;
using AlgoWorker.Hubs;
using AlgoWorker.Models;
using AlgoWorker.Services;
using AlgoWorker.Services.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AlgoWorker.BankNifty;

/// <summary>
/// The Upside Reversal strategy (5-min, bullish only, buys CE) for Bank Nifty. It reuses the exact same strategy classes as Nifty
/// (ReversalSignalEngine, ReversalPositionTracker) -- only the settings (BankNiftyOptions via IReversalSettings), the instrument key and the alert/DB tags differ.
/// It does not own a candle stream: BankNiftyService feeds it the same 5-min candles the ORB strategy uses.
/// </summary>
public sealed class BankNiftyReversalPipeline
{
    private readonly BankNiftyOptions _options;
    private readonly UpstoxOptions _upstox;
    private readonly UpstoxRestClient _rest;
    private readonly ReversalSignalEngine _engine;
    private readonly ReversalPositionTracker _tracker;
    private readonly ILogger<BankNiftyReversalPipeline> _logger;

    public BankNiftyReversalPipeline(
        OptionInstrumentResolver resolver, UpstoxRestClient rest, UpstoxWebSocketClient ws, TelegramAlertService telegram,
        IPositionRepository positions, ISignalLogRepository signalLog, IHubContext<PositionHub> hub, IClock clock,
        IOptions<BankNiftyOptions> options, IOptions<UpstoxOptions> upstox,
        ILoggerFactory loggerFactory, ILogger<BankNiftyReversalPipeline> logger)
    {
        _options = options.Value;
        _upstox = upstox.Value;
        _rest = rest;
        _logger = logger;

        _engine = new ReversalSignalEngine(_options, loggerFactory.CreateLogger("AlgoWorker.BankNifty.ReversalEngine"));
        _tracker = new ReversalPositionTracker(resolver, rest, ws, telegram, _options,
            new EmaTarget("Bank Nifty", Underlyings.BankNifty, _upstox.BankNiftyInstrumentKey),
            positions, signalLog, hub, clock, loggerFactory.CreateLogger("AlgoWorker.BankNifty.ReversalTracker"));

        _engine.ReversalConfirmed += s => _ = SafeAsync(ct => _tracker.OnReversalConfirmedAsync(s, ct), "OnReversalConfirmedAsync");
    }

    public ReversalPositionTracker Tracker => _tracker;

    /// <summary>While true the engine builds state (EMAs, previous-day low, today's low) but never raises a signal.</summary>
    public bool IsSeeding { get => _engine.IsSeeding; set => _engine.IsSeeding = value; }

    /// <summary>
    /// Replays the previous trading day(s) of 5-min candles straight into the engine (EMA21/50 warm-up + previous-day low).
    /// Call this BEFORE today's candles are replayed, with <see cref="IsSeeding"/> already true.
    /// </summary>
    public async Task SeedHistoryAsync(CancellationToken ct)
    {
        var daysToKeep = Math.Max(1, _options.ReversalSeedLookbackTradingDays);
        var yesterday = MarketTime.Today.AddDays(-1);
        var fetched = await _rest.GetHistoricalCandles5MinAsync(_upstox.BankNiftyInstrumentKey, yesterday.AddDays(-10), yesterday, ct);

        var recentDates = fetched.Select(c => DateOnly.FromDateTime(c.OpenTime.Date))
            .Distinct().OrderByDescending(d => d).Take(daysToKeep).ToHashSet();
        var history = fetched.Where(c => recentDates.Contains(DateOnly.FromDateTime(c.OpenTime.Date)))
            .OrderBy(c => c.OpenTime).ToList();

        foreach (var c in history)
        {
            c.IsClosed = true;
            _engine.OnCandleClosed(c);
        }
        _logger.LogInformation("Bank Nifty Reversal: replayed {Count} 5-min candles from {Days} previous day(s).", history.Count, recentDates.Count);
    }

    /// <summary>One closed 5-min candle. The engine part is synchronous (so the seed replay stays synchronous); the tracker only runs live.</summary>
    public Task OnFiveMinCandleAsync(Candle candle, CancellationToken ct)
    {
        _engine.OnCandleClosed(candle);
        return IsSeeding ? Task.CompletedTask : _tracker.OnCandleClosedAsync(candle.OpenTime, ct);
    }

    /// <summary>Bank Nifty index tick: SL / T1 / T2 are index SPOT levels, checked on every tick.</summary>
    public Task OnIndexTick(MarketTick tick, CancellationToken ct) => _tracker.OnSpotTick(tick.LastTradedPrice, ct);

    public Task OnOptionTick(MarketTick tick, CancellationToken ct) => _tracker.OnOptionTick(tick, ct);

    public Task CloseForMarketCloseAsync(CancellationToken ct) => _tracker.CloseForMarketCloseAsync(ct);

    private async Task SafeAsync(Func<CancellationToken, Task> action, string name)
    {
        try { await action(CancellationToken.None); }
        catch (Exception ex) { _logger.LogError(ex, "Bank Nifty Reversal: error in fire-and-forget operation {Operation}", name); }
    }
}