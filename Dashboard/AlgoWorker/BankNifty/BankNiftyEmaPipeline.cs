using AlgoData.Models;
using AlgoWorker.Configuration;
using AlgoWorker.Hubs;
using AlgoWorker.Models;
using AlgoWorker.Services;
using AlgoWorker.Services.Indicators;
using AlgoWorker.Services.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AlgoWorker.BankNifty;

/// <summary>
/// The EMA21/50 + ADX crossover strategy for Bank Nifty. It reuses the exact same strategy classes as Nifty
/// (IndicatorEngine, SignalEngine, HistoricalSeederService, VirtualPositionTracker) -- only the settings
/// (BankNiftyOptions via IEmaSettings), the 15-min candle stream, the instrument key and the alert/DB tags differ.
/// BankNiftyService drives it: seed, index ticks, option ticks, market close.
/// </summary>
public sealed class BankNiftyEmaPipeline
{
    private readonly BankNiftyOptions _options;
    private readonly UpstoxOptions _upstox;
    private readonly CandleAggregatorService _aggregator;
    private readonly IndicatorEngine _indicators;
    private readonly SignalEngine _signals;
    private readonly VirtualPositionTracker _tracker;
    private readonly HistoricalSeederService _seeder;
    private readonly ExpiryProvider _expiries;
    private readonly TelegramAlertService _telegram;
    private readonly ILogger<BankNiftyEmaPipeline> _logger;
    private volatile bool _seeding;

    public BankNiftyEmaPipeline(
        OptionInstrumentResolver resolver, UpstoxRestClient rest, UpstoxWebSocketClient ws, TelegramAlertService telegram,
        ExpiryProvider expiries, IPositionRepository positions, ISignalLogRepository signalLog, IHubContext<PositionHub> hub,
        IClock clock, IOptions<BankNiftyOptions> options, IOptions<UpstoxOptions> upstox,
        ILoggerFactory loggerFactory, ILogger<BankNiftyEmaPipeline> logger)
    {
        _options = options.Value;
        _upstox = upstox.Value;
        _telegram = telegram;
        _expiries = expiries;
        _logger = logger;

        _aggregator = new CandleAggregatorService(TimeSpan.FromMinutes(_options.EmaCandleMinutes));
        _indicators = new IndicatorEngine(_options);
        _signals = new SignalEngine(_options, loggerFactory.CreateLogger("AlgoWorker.BankNifty.EmaSignalEngine"));
        _tracker = new VirtualPositionTracker(resolver, rest, ws, telegram, _options,
            new EmaTarget("Bank Nifty", Underlyings.BankNifty, _upstox.BankNiftyInstrumentKey),
            positions, signalLog, hub, clock, loggerFactory.CreateLogger("AlgoWorker.BankNifty.EmaTracker"));
        _seeder = new HistoricalSeederService(rest, _aggregator, _options, _upstox.BankNiftyInstrumentKey, clock,
            loggerFactory.CreateLogger("AlgoWorker.BankNifty.EmaSeeder"));

        _aggregator.CandleClosed += OnCandleClosed;
        _signals.SignalConfirmed += s => _ = SafeAsync(ct => _tracker.OnSignalConfirmedAsync(s, ct), "OnSignalConfirmedAsync");
    }

    public VirtualPositionTracker Tracker => _tracker;

    /// <summary>Replays recent 15-min history so EMA50/ADX are warm. No alerts or trades come out of the replay.</summary>
    public async Task SeedAsync(CancellationToken ct)
    {
        _signals.IsSeeding = true;
        _seeding = true;
        try { await _seeder.SeedAsync(ct); }
        finally { _seeding = false; _signals.IsSeeding = false; }

        var catchUp = _signals.EvaluateCatchUpSignal(_indicators);
        if (catchUp is not null)
            await _telegram.SendStatusAsync(
                $"Bank Nifty: {catchUp.Direction} is already active (originally crossed at {AlgoData.Models.MarketTime.ToIst(catchUp.ConfirmedAtCandleTime):dd-MMM-yyyy hh:mm tt}). " +
                "Older crossover found at startup -- NO trade taken on it. Only the next FRESH crossover will trigger a trade.", ct);
    }

    public void OnIndexTick(MarketTick tick) => _aggregator.OnTick(tick);

    public Task OnOptionTick(MarketTick tick, CancellationToken ct) => _tracker.OnOptionTick(tick, ct);

    public async Task CloseForMarketCloseAsync(CancellationToken ct)
    {
        _aggregator.ForceCloseCurrentCandle();
        await Task.Delay(200, ct);   // let the last candle's handler finish before the force-close
        await _tracker.CloseAllForMarketCloseAsync(ct);
    }

    // During the seed replay this must stay fully synchronous (indicators, then signals -- no awaits), exactly like the Nifty path,
    // because SeedAsync flips IsSeeding back as soon as the replay returns. Live, the position reacts first (frees the slot), then the engine may signal.
    private async void OnCandleClosed(Candle candle)
    {
        try
        {
            var snapshot = _indicators.OnCandleClosed(candle);
            if (_seeding)
            {
                _signals.OnIndicatorSnapshot(_indicators, snapshot);
                return;
            }

            var isExpiryDay = false;
            if (_tracker.AnyLegOpen)
            {
                var day = DateOnly.FromDateTime(candle.OpenTime.Date);
                isExpiryDay = await _expiries.ResolveCurrentAsync(_upstox.BankNiftyInstrumentKey, day, CancellationToken.None) == day;
            }

            await _tracker.OnCandleClosedAsync(_indicators, snapshot, isExpiryDay, CancellationToken.None);
            _signals.OnIndicatorSnapshot(_indicators, snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bank Nifty EMA: error handling closed {Minutes}-min candle at {Time}", _options.EmaCandleMinutes, candle.OpenTime);
        }
    }

    private async Task SafeAsync(Func<CancellationToken, Task> action, string name)
    {
        try { await action(CancellationToken.None); }
        catch (Exception ex) { _logger.LogError(ex, "Bank Nifty EMA: error in fire-and-forget operation {Operation}", name); }
    }
}