using AlgoData.Models;
using AlgoWorker.Configuration;
using AlgoWorker.Hubs;
using AlgoWorker.Models;
using AlgoWorker.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AlgoWorker.BankNifty;

/// <summary>
/// The Bank Nifty pipeline, hosted inside the AlgoWorker process next to the Nifty <c>Worker</c> but sharing none of its
/// strategy code. It does NOT own the WebSocket: the Nifty Worker runs the single shared feed (subscribing the Bank Nifty
/// index key too) and this service just listens to the same <c>TickReceived</c> event, picking out its own instruments.
/// What it owns: daily opening-range seeding, the 5-min candle stream, session-end clean-up, and the ORB -> tracker wiring.
/// Everything here is wrapped so a Bank Nifty failure can never take the Nifty pipeline down.
/// </summary>
public sealed class BankNiftyService : BackgroundService, IMarketFeedConsumer
{
    private readonly UpstoxWebSocketClient _ws;
    private readonly BankNiftyCandleAggregator _aggregator;
    private readonly OrbSignalEngine _orb;
    private readonly BankNiftyPositionTracker _tracker;
    private readonly BankNiftyEmaPipeline _ema;
    private readonly OpeningRangeSeederService _seeder;
    private readonly MarketHoursService _marketHours;
    private readonly BankNiftyAlertService _telegram;
    private readonly IHubContext<PositionHub> _hub;
    private readonly UpstoxOptions _upstox;
    private readonly BankNiftyOptions _options;
    private readonly ILogger<BankNiftyService> _logger;

    private bool _seededToday;
    private DateOnly _lastSeededDate;
    private DateTimeOffset _lastSpotBroadcast = DateTimeOffset.MinValue;
    private bool _loggedFirstIndexTick;
    private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;

    /// <summary>The Bank Nifty index, subscribed on the shared feed by the Nifty Worker -- only while this pipeline is enabled.</summary>
    public IReadOnlyCollection<string> FeedInstrumentKeys =>
        _options.Enabled ? new[] { _upstox.BankNiftyInstrumentKey } : Array.Empty<string>();

    public BankNiftyService(
        UpstoxWebSocketClient ws, BankNiftyCandleAggregator aggregator, OrbSignalEngine orb,
        BankNiftyPositionTracker tracker, OpeningRangeSeederService seeder, MarketHoursService marketHours,
        BankNiftyEmaPipeline ema, BankNiftyAlertService telegram, IHubContext<PositionHub> hub,
        IOptions<UpstoxOptions> upstox, IOptions<BankNiftyOptions> options, ILogger<BankNiftyService> logger)
    {
        _ws = ws; _aggregator = aggregator; _orb = orb; _tracker = tracker; _ema = ema; _seeder = seeder;
        _marketHours = marketHours; _telegram = telegram; _hub = hub;
        _upstox = upstox.Value; _options = options.Value; _logger = logger;

        _ws.TickReceived += OnTickReceived;
        _aggregator.CandleClosed += OnFiveMinCandleClosed;
        _orb.BreakoutConfirmed += s => _ = SafeAsync(ct => _tracker.OnBreakoutConfirmedAsync(s, ct), "OnBreakoutConfirmedAsync");
        _orb.OpeningRangeCaptured += r => _ = SafeAsync(ct => _telegram.SendOpeningRangeAsync(r, ct), "SendOpeningRange");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting (Program.cs seeds settings / closes orphans before RunAsync) before we look at options.
        await Task.Yield();

        if (!_options.Enabled)
        {
            _logger.LogWarning("BankNifty:Enabled is false -- Bank Nifty pipeline idle.");
            return;
        }

        _logger.LogInformation("Bank Nifty pipeline starting (shared WebSocket feed, own ORB strategy).");
        await _telegram.SendStatusAsync("Bank Nifty pipeline started.", stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOneCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (UnauthorizedTokenException)
            {
                // The Nifty Worker already alerts about the missing token; just wait and retry.
                _logger.LogWarning("Bank Nifty: Upstox token missing/expired -- waiting for a fresh one.");
                await DelaySafe(TimeSpan.FromMinutes(2), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in the Bank Nifty loop -- will retry shortly.");
                await DelaySafe(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task RunOneCycleAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var inWindow = now.ToIst().DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
                       && TimeOfDay.Of(now) >= TimeOfDay.Parse(_options.SessionStartTime)
                       && TimeOfDay.Of(now) <= TimeOfDay.Parse(_options.SessionEndTime);

        if (!inWindow)
        {
            if (_seededToday)
            {
                _aggregator.ForceCloseCurrentCandle();
                await _tracker.CloseForMarketCloseAsync(ct);
                try { await _ema.CloseForMarketCloseAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Bank Nifty EMA: error closing for the day."); }
                _seededToday = false;
                await _telegram.SendStatusAsync("Market closed for the day.", ct);
            }
            await DelaySafe(TimeSpan.FromMinutes(1), ct);
            return;
        }

        var today = AlgoData.Models.MarketTime.Today;
        if (!(_seededToday && _lastSeededDate == today) && await _marketHours.IsMarketOpenAsync(ct))
        {
            _orb.IsSeeding = true;
            try { await _seeder.SeedAsync(ct); }
            finally { _orb.IsSeeding = false; }

            if (_orb.HasRange)
                await _telegram.SendStatusAsync($"Restored today's opening range from history: High {_orb.RangeHigh:N2}, Low {_orb.RangeLow:N2}. Breakouts that already happened are not traded.", ct);

            // EMA21/50 history (15-min): a failure here must not stop the ORB strategy, so it has its own try/catch.
            try { await _ema.SeedAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Bank Nifty EMA: seeding failed -- EMA strategy may warm up late today (ORB unaffected).");
            }

            _seededToday = true;
            _lastSeededDate = today;
        }

        await DelaySafe(TimeSpan.FromSeconds(10), ct);
    }

    private static async Task DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }

    // Subscribed to the shared feed's TickReceived (the Nifty Worker owns the connection). async void with a full
    // try/catch so an exception here can never reach the shared event invocation and affect the Nifty handlers.
    private async void OnTickReceived(MarketTick tick)
    {
        try
        {
            if (!_options.Enabled) return;

            if (tick.InstrumentKey == _upstox.BankNiftyInstrumentKey)
            {
                if (!_loggedFirstIndexTick)
                {
                    _loggedFirstIndexTick = true;
                    _logger.LogInformation("Bank Nifty: first index tick received from the shared feed ({Price:N2}).", tick.LastTradedPrice);
                }
                _aggregator.OnTick(tick);
                _ema.OnIndexTick(tick);
                if (DateTimeOffset.UtcNow - _lastHeartbeat >= TimeSpan.FromSeconds(60))
                {
                    _lastHeartbeat = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Spot {Price:N2} | opening range {Range} | open position: {Open}",
                        tick.LastTradedPrice,
                        _orb.HasRange ? $"{_orb.RangeLow:N2} - {_orb.RangeHigh:N2}" : "not captured yet",
                        _tracker.GetOpenPositionsSnapshot().Any() ? "yes" : "none");
                }
                await BroadcastSpotAsync(tick.LastTradedPrice);
            }
            else
            {
                await _tracker.OnOptionTick(tick, CancellationToken.None);   // no-op unless it's the open Bank Nifty ORB option
                await _ema.OnOptionTick(tick, CancellationToken.None);       // no-op unless it's an open Bank Nifty EMA leg
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bank Nifty: error handling tick for {Instrument}", tick.InstrumentKey);
        }
    }

    // Order matters: the open position reacts to the candle first (force exit frees the slot),
    // then the engine may raise a fresh breakout.
    private async void OnFiveMinCandleClosed(Candle candle)
    {
        try
        {
            _logger.LogInformation("5-min candle {Time:HH:mm} closed: O {Open:N2} H {High:N2} L {Low:N2} C {Close:N2} | range {Range}",
                candle.OpenTime, candle.Open, candle.High, candle.Low, candle.Close,
                _orb.HasRange ? $"{_orb.RangeLow:N2} - {_orb.RangeHigh:N2}" : "not captured yet");
            await _tracker.OnCandleClosedAsync(candle, CancellationToken.None);
            _orb.OnCandleClosed(candle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bank Nifty: error handling closed 5-min candle at {Time}", candle.OpenTime);
        }
    }

    private async Task BroadcastSpotAsync(decimal price)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSpotBroadcast < TimeSpan.FromSeconds(1)) return;
        _lastSpotBroadcast = now;
        await PositionBroadcast.PublishSpotAsync(_hub, price, now, AlgoData.Models.Underlyings.BankNifty, _logger);
    }

    private async Task SafeAsync(Func<CancellationToken, Task> action, string name)
    {
        try { await action(CancellationToken.None); }
        catch (Exception ex) { _logger.LogError(ex, "Bank Nifty: error in fire-and-forget operation {Operation}", name); }
    }
}