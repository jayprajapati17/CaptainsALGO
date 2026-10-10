using AlgoWorker.Configuration;
using AlgoWorker.Hubs;
using AlgoWorker.Models;
using AlgoWorker.Services;
using AlgoWorker.Services.Indicators;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AlgoWorker;

public sealed class Worker : BackgroundService
{
    private readonly UpstoxRestClient _restClient;
    private readonly UpstoxWebSocketClient _webSocketClient;
    private readonly HistoricalSeederService _seeder;
    private readonly CandleAggregatorService _aggregator;
    private readonly IndicatorEngine _indicatorEngine;
    private readonly SignalEngine _signalEngine;
    private readonly VirtualPositionTracker _positionTracker;
    private readonly MarketHoursService _marketHours;
    private readonly TelegramAlertService _telegram;
    private readonly ExpiryResolver _expiryResolver;
    private readonly UpstoxOptions _upstoxOptions;
    // >>> NEW: Previous-Day High/Low Breakout strategy pipeline (5-min, intraday).
    private readonly FiveMinCandleAggregatorService _fiveMinAggregator;
    private readonly BreakoutSignalEngine _breakoutEngine;
    private readonly BreakoutPositionTracker _breakoutPositionTracker;
    private readonly ReversalSignalEngine _reversalEngine;
    private readonly ReversalPositionTracker _reversalPositionTracker;
    private readonly FiveMinHistoricalSeederService _fiveMinSeeder;
    // >>> NEW: 3-Minute MACD strategy pipeline (own 3-min candles, own MACD engine).
    private readonly ThreeMinCandleAggregatorService _threeMinAggregator;
    private readonly MacdEngine _macdEngine;
    private readonly MacdSignalEngine _macdSignalEngine;
    private readonly MacdPositionTracker _macdPositionTracker;
    private readonly MacdHistoricalSeederService _macdSeeder;
    private readonly CprAnalysisService _cprAnalysisService;
    private readonly AlgoWorker.Services.Persistence.ICprRepository _cprRepository;
    // >>> NEW (Task 8): used to push the live Nifty spot to the Dashboard header.
    private readonly IHubContext<PositionHub> _hub;
    private DateTimeOffset _lastSpotBroadcast = DateTimeOffset.MinValue;
    private readonly ILogger<Worker> _logger;

    private bool _seededToday;
    private bool _tokenWarningSentToday;
    private DateOnly _lastSeededDate;

    public Worker(
        UpstoxRestClient restClient,
        UpstoxWebSocketClient webSocketClient,
        HistoricalSeederService seeder,
        CandleAggregatorService aggregator,
        IndicatorEngine indicatorEngine,
        SignalEngine signalEngine,
        VirtualPositionTracker positionTracker,
        MarketHoursService marketHours,
        TelegramAlertService telegram,
        ExpiryResolver expiryResolver,
        FiveMinCandleAggregatorService fiveMinAggregator,
        BreakoutSignalEngine breakoutEngine,
        BreakoutPositionTracker breakoutPositionTracker,
        ReversalSignalEngine reversalEngine,
        ReversalPositionTracker reversalPositionTracker,
        FiveMinHistoricalSeederService fiveMinSeeder,
        ThreeMinCandleAggregatorService threeMinAggregator,
        MacdEngine macdEngine,
        MacdSignalEngine macdSignalEngine,
        MacdPositionTracker macdPositionTracker,
        MacdHistoricalSeederService macdSeeder,
        CprAnalysisService cprAnalysisService,
        AlgoWorker.Services.Persistence.ICprRepository cprRepository,
        IHubContext<PositionHub> hub,
        IOptions<UpstoxOptions> upstoxOptions,
        ILogger<Worker> logger)
    {
        _restClient = restClient;
        _webSocketClient = webSocketClient;
        _seeder = seeder;
        _aggregator = aggregator;
        _indicatorEngine = indicatorEngine;
        _signalEngine = signalEngine;
        _positionTracker = positionTracker;
        _marketHours = marketHours;
        _telegram = telegram;
        _expiryResolver = expiryResolver;
        _fiveMinAggregator = fiveMinAggregator;
        _breakoutEngine = breakoutEngine;
        _breakoutPositionTracker = breakoutPositionTracker;
        _reversalEngine = reversalEngine;
        _reversalPositionTracker = reversalPositionTracker;
        _fiveMinSeeder = fiveMinSeeder;
        _threeMinAggregator = threeMinAggregator;
        _macdEngine = macdEngine;
        _macdSignalEngine = macdSignalEngine;
        _macdPositionTracker = macdPositionTracker;
        _macdSeeder = macdSeeder;
        _cprAnalysisService = cprAnalysisService;
        _cprRepository = cprRepository;
        _hub = hub;
        _upstoxOptions = upstoxOptions.Value;
        _logger = logger;

        WireEvents();
    }

    private void WireEvents()
    {
        _aggregator.CandleClosed += OnCandleClosedSync;
        _webSocketClient.TickReceived += OnTickReceived;
        _webSocketClient.Connected += () => _logger.LogInformation("WebSocket connected event fired.");
        _webSocketClient.Disconnected += ex => _logger.LogWarning("WebSocket disconnected: {Message}", ex?.Message);
        _signalEngine.SignalConfirmed += signal => _ = SafeAsync(ct => _positionTracker.OnSignalConfirmedAsync(signal, ct), "OnSignalConfirmedAsync");

        // >>> NEW: breakout strategy wiring -- own 5-min candle stream, own signal engine, own position tracker.
        _fiveMinAggregator.CandleClosed += OnFiveMinCandleClosed;
        _breakoutEngine.BreakoutConfirmed += signal => _ = SafeAsync(ct => _breakoutPositionTracker.OnBreakoutConfirmedAsync(signal, ct), "OnBreakoutConfirmedAsync");

        // >>> NEW: Upside Reversal strategy -- same 5-min candle stream.
        _reversalEngine.ReversalConfirmed += signal => _ = SafeAsync(ct => _reversalPositionTracker.OnReversalConfirmedAsync(signal, ct), "OnReversalConfirmedAsync");

        // >>> NEW: MACD strategy wiring -- own 3-min candle stream, own MACD engine, own signal/position pipeline.
        _threeMinAggregator.CandleClosed += OnThreeMinCandleClosed;
        _macdSignalEngine.MacdSignalConfirmed += signal => _ = SafeAsync(ct => _macdPositionTracker.OnMacdConfirmedAsync(signal, ct), "OnMacdConfirmedAsync");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Nifty EMA Alert Bot starting up...");
        await _telegram.SendStatusAsync("Bot starting up.", stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOneCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (UnauthorizedTokenException)
            {
                await HandleTokenExpiredAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in main loop -- will retry shortly.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        _logger.LogInformation("Nifty EMA Alert Bot stopping.");
    }

    /// <summary>
    /// One pass: check market hours (cheap local check first, then the live
    /// Exchange Status API), and either idle or run the live pipeline.
    /// </summary>
    private async Task RunOneCycleAsync(CancellationToken ct)
    {
        if (!_marketHours.IsWithinConfiguredWindow(DateTimeOffset.Now))
        {
            await IdleUntilNextWindowAsync(ct);
            return;
        }

        var isOpen = await _marketHours.IsMarketOpenAsync(ct);
        if (!isOpen)
        {
            _logger.LogInformation("Within configured time window but Upstox reports market not open (holiday?). Idling 5 min.");
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            return;
        }

        await EnsureSeededForTodayAsync(ct);

        _logger.LogInformation("Market is open -- starting live WebSocket feed.");
        await _telegram.SendStatusAsync("Market open. Live tracking started.", ct);

        var instrumentKeys = new[] { _upstoxOptions.NiftyInstrumentKey };
        await _webSocketClient.RunAsync(instrumentKeys, ct);

        // RunAsync only returns when `ct` is cancelled or after exhausting retries;
        // if we get here mid-day it means the outer loop will just re-evaluate
        // market hours and reconnect on the next pass.
    }

    private async Task EnsureSeededForTodayAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (_seededToday && _lastSeededDate == today) return;

        // >>> NEW: tell the SignalEngine we're replaying history, not live data,
        // so it doesn't fire real alerts / open virtual positions for crossovers
        // that already happened before the bot even started today.
        _signalEngine.IsSeeding = true;
        try
        {
            await _seeder.SeedAsync(ct);
        }
        finally
        {
            _signalEngine.IsSeeding = false;
        }

        // >>> CHANGED (per your request): a crossover found already-active right after
        // seeding is now INFORMATIONAL ONLY -- it does NOT open a trade anymore. Only a
        // FRESH crossover detected live, during market hours, opens a position. This just
        // tells you what's currently active so you're not caught by surprise; act on it
        // yourself if you want to.
        var catchUpSignal = _signalEngine.EvaluateCatchUpSignal(_indicatorEngine);
        if (catchUpSignal is not null)
        {
            await _telegram.SendStatusAsync(
                $"{catchUpSignal.Direction} is already active (originally crossed at {catchUpSignal.ConfirmedAtCandleTime:dd-MMM-yyyy hh:mm tt}). " +
                "This is an older crossover found at startup -- NO trade was taken on it. Only the next FRESH crossover will trigger a trade.", ct);
        }

        // >>> REMOVED: this used to fetch yesterday's High/Low for the breakout
        // strategy (GetPreviousTradingDayHighLowAsync / SetPreviousDayLevels).
        // BreakoutSignalEngine was redesigned to "Opening Range Breakout" --
        // it now captures its own reference range from TODAY's first 5-min
        // candle automatically (see BreakoutSignalEngine.OnCandleClosed), so
        // no external level-setting call is needed here anymore. Those two
        // methods no longer exist on UpstoxRestClient/BreakoutSignalEngine.
        //
        // NOTE: today's already-elapsed 5-min candles are NOT currently
        // replayed at startup, so a mid-session restart will miss the
        // 09:15-09:20 opening-range candle and the breakout strategy simply
        // won't fire for the rest of that day. Flagging this as a known gap --
        // say the word if you'd like a proper catch-up seed added here (same
        // shape as the EMA/MACD seeders), similar to how BreakoutSignalEngine's
        // own IsSeeding/ConsumeCatchUpSignal are already built to support it.

        // >>> NEW (per your request): compute + save TODAY's CPR (Central Pivot Range),
        // derived from YESTERDAY's completed High/Low/Close. This is what the Live
        // page's header strip reads (Task 8's CprStrip) -- previously nothing ever
        // called CprAnalysisService, so this table stayed empty and the strip always
        // showed "Not available yet". Non-fatal on failure -- Telegram/trading logic
        // is unaffected either way.
        try
        {
            var prevDay = await _restClient.GetPreviousTradingDayOhlcAsync(_upstoxOptions.NiftyInstrumentKey, ct);
            if (prevDay is { } p)
            {
                var levels = _cprAnalysisService.Compute(p.High, p.Low, p.Close, forTradingDay: today);
                await _cprRepository.SaveAsync(new AlgoData.Models.DailyCprEntity
                {
                    ForTradingDay = levels.ForTradingDay,
                    Label = AlgoData.Models.CprLabel.TodaysCpr,
                    SourceHigh = levels.SourceHigh,
                    SourceLow = levels.SourceLow,
                    SourceClose = levels.SourceClose,
                    CPRPivot = levels.Pivot,
                    Tc = levels.Tc,
                    Bc = levels.Bc,
                    R1 = levels.R1,
                    S1 = levels.S1,
                    R2 = levels.R2,
                    S2 = levels.S2,
                    WidthPercent = levels.WidthPercent,
                    Reading = levels.Reading,
                    BiasNote = levels.BiasNote,
                    ComputedAt = DateTimeOffset.Now
                }, ct);
            }
            else
            {
                _logger.LogWarning("Could not fetch previous trading day's OHLC -- Today's CPR not computed.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute/save Today's CPR.");
        }

        _seededToday = true;
        _lastSeededDate = today;
        _tokenWarningSentToday = false;

        // >>> NEW: seed the MACD strategy's 3-min candles (previous 2 trading
        // days + today so far). Non-fatal on failure -- MACD will just warm up
        // live over the next ~1h45m instead, EMA/Breakout are unaffected.
        _macdSignalEngine.IsSeeding = true;
        try
        {
            await _macdSeeder.SeedAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MACD 3-min seeding failed -- strategy will warm up live instead.");
            await _telegram.SendWarningAsync("Could not seed MACD 3-min history -- it will warm up live over the next ~1h45m instead of trading from market open.", ct);
        }
        finally
        {
            _macdSignalEngine.IsSeeding = false;
        }

        // >>> NEW: replay 5-min candles (previous days + today so far) into the ORB and Reversal engines.
        // Fixes the mid-day-restart gap for ORB (the real 09:15 range is restored, and a breakout that already
        // happened never trades) and warms up EMA21/50 + previous-day low for the Reversal strategy.
        _breakoutEngine.IsSeeding = true;
        _reversalEngine.IsSeeding = true;
        try
        {
            await _fiveMinSeeder.SeedAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "5-min seeding failed -- ORB / Reversal will build their state from live candles instead.");
            await _telegram.SendWarningAsync("Could not seed 5-min history -- ORB and Reversal strategies will build their state live (Reversal needs ~1 hour of live candles before it can trade).", ct);
        }
        finally
        {
            _breakoutEngine.IsSeeding = false;
            _reversalEngine.IsSeeding = false;
        }
    }

    private async Task IdleUntilNextWindowAsync(CancellationToken ct)
    {
        // If the session just ended, make sure the last candle + any open
        // virtual positions are closed out cleanly before going idle.
        if (_seededToday)
        {
            _aggregator.ForceCloseCurrentCandle();
            _fiveMinAggregator.ForceCloseCurrentCandle(); // >>> NEW
            _threeMinAggregator.ForceCloseCurrentCandle(); // >>> NEW
            await _positionTracker.CloseAllForMarketCloseAsync(ct);
            await _breakoutPositionTracker.CloseForMarketCloseAsync(ct); // >>> NEW
            await _reversalPositionTracker.CloseForMarketCloseAsync(ct);
            await _macdPositionTracker.CloseForMarketCloseAsync(ct); // >>> NEW
            _seededToday = false;
            await _telegram.SendStatusAsync("Market closed for the day. Bot going idle until next session.", ct);
        }

        await Task.Delay(TimeSpan.FromMinutes(1), ct);
    }

    // >>> NEW (Task 8): pushes the live Nifty spot to the Dashboard header over
    // PositionHub's "SpotChanged" event, throttled to ~1/sec so the tick stream
    // doesn't flood the hub. Non-fatal on failure -- never interrupts tick handling.
    private async Task BroadcastSpotAsync(decimal price)
    {
        // >>> FIXED: was DateTimeOffset.UtcNow -- the only UTC usage anywhere in the
        // system; every tracker, TelegramAlertService, and market-hours check uses
        // local DateTimeOffset.Now. To be precise: this particular throttle's math
        // was NOT actually broken by it (DateTimeOffset subtraction compares the
        // absolute instant, so it's offset-safe either way) -- but mixing UTC and
        // local "now" calls elsewhere in a codebase is a real, common source of bugs
        // (e.g. an off-by-offset error the moment someone compares one of these
        // timestamps against a local DateTimeOffset.Now-based value, or logs/persists
        // it expecting local time). Standardized on local Now to match everywhere else.
        var now = DateTimeOffset.Now;
        if (now - _lastSpotBroadcast < TimeSpan.FromSeconds(1))
            return;

        _lastSpotBroadcast = now;
        try
        {
            await _hub.Clients.All.SendAsync("SpotChanged", new { price, time = DateTimeOffset.Now });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to broadcast SpotChanged.");
        }
    }

    // >>> CHANGED: async void (same pattern as OnFiveMinCandleClosed below) so we
    // can await the breakout tracker's per-tick stop-loss/trailing-stop check --
    // it may need to fire an immediate exit (Telegram alert + DB write), not just
    // record the price.
    private async void OnTickReceived(MarketTick tick)
    {
        try
        {
            if (tick.InstrumentKey == _upstoxOptions.NiftyInstrumentKey)
            {
                _aggregator.OnTick(tick);
                _fiveMinAggregator.OnTick(tick); // same Nifty ticks also feed the breakout strategy's 5-min candles
                _threeMinAggregator.OnTick(tick); // >>> NEW: same Nifty ticks also feed the MACD strategy's 3-min candles
                await _reversalPositionTracker.OnSpotTick(tick.LastTradedPrice, CancellationToken.None); // Reversal SL/T1/T2 are Nifty spot levels
                await BroadcastSpotAsync(tick.LastTradedPrice); // >>> NEW (Task 8): Dashboard header spot
            }
            else
            {
                // Any other subscribed instrument is an option contract being virtually
                // tracked by one of the two strategies -- harmless no-op for whichever
                // tracker doesn't own that instrument key.
                await _positionTracker.OnOptionTick(tick, CancellationToken.None); // >>> CHANGED: now checks per-leg SL/trailing-SL per tick
                await _breakoutPositionTracker.OnOptionTick(tick, CancellationToken.None); // >>> CHANGED: now checks SL/trailing-SL per tick
                await _reversalPositionTracker.OnOptionTick(tick, CancellationToken.None);
                await _macdPositionTracker.OnOptionTick(tick, CancellationToken.None); // >>> NEW: MACD step-trailing SL per tick
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling tick for {Instrument}", tick.InstrumentKey);
        }
    }

    // >>> NEW: drives the breakout strategy off the 5-min candle stream --
    // independent of, and running alongside, the EMA strategy's 15-min pipeline.
    private async void OnFiveMinCandleClosed(Candle candle)
    {
        try
        {
            _breakoutEngine.OnCandleClosed(candle);
            await _breakoutPositionTracker.OnCandleClosedAsync(candle.OpenTime, CancellationToken.None);
            _reversalEngine.OnCandleClosed(candle);
            await _reversalPositionTracker.OnCandleClosedAsync(candle.OpenTime, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling closed 5-min candle at {Time}", candle.OpenTime);
        }
    }

    // >>> NEW: drives the 3-Minute MACD strategy -- independent 3-min candle
    // stream, own MACD(12,26,9) engine. MacdEngine.OnCandleClosed returns null
    // while it's still warming up (needs ~35 candles for the Signal line), so
    // the signal engine and position tracker are only fed once it has a value.
    private async void OnThreeMinCandleClosed(Candle candle)
    {
        try
        {
            var snapshot = _macdEngine.OnCandleClosed(candle);
            if (snapshot is null)
                return;

            // 1) Check/close any existing MACD position first (opposite crossover, cutoff, updates).
            await _macdPositionTracker.OnCandleClosedAsync(_macdEngine, snapshot, CancellationToken.None);

            // 2) THEN let the signal engine possibly raise a fresh confirmed signal for the freed slot.
            _macdSignalEngine.OnMacdSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling closed 3-min candle at {Time}", candle.OpenTime);
        }
    }

    // >>> CHANGED: was `void OnCandleClosedSync` firing both steps as separate
    // fire-and-forget tasks with no guaranteed order. Now `async void` (safe
    // here -- it's a top-level event handler with its own try/catch) so we can
    // AWAIT the exit-check BEFORE letting the SignalEngine possibly open a new
    // position on the same candle. This matters because we only allow ONE
    // active virtual position at a time: if a Death Cross confirms on the same
    // candle where an open Golden Cross position's "opposite crossover" exit
    // also fires, we need that exit to free the slot FIRST, so the new signal
    // can immediately take the newly-freed slot instead of being ignored.
    private async void OnCandleClosedSync(Candle candle)
    {
        try
        {
            var snapshot = _indicatorEngine.OnCandleClosed(candle);
            var isExpiryDay = IsAnyTrackedExpiryToday(candle.OpenTime);

            // 1) Evaluate/close any existing position first (frees the single slot).
            await _positionTracker.OnCandleClosedAsync(_indicatorEngine, snapshot, isExpiryDay, CancellationToken.None);

            // 2) THEN let the SignalEngine possibly raise a new confirmed signal,
            //    which (via the event wired in WireEvents) can now open into the freed slot.
            _signalEngine.OnIndicatorSnapshot(_indicatorEngine, snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling closed candle at {Time}", candle.OpenTime);
        }
    }

    private bool IsAnyTrackedExpiryToday(DateTimeOffset candleTime)
    {
        // Conservative check: is today itself a Tuesday (the only day any
        // next-week-resolved expiry could ever land on)? Cheap and sufficient
        // for the forced-exit safety net.
        return candleTime.DayOfWeek == DayOfWeek.Tuesday;
    }

    private async Task HandleTokenExpiredAsync(CancellationToken ct)
    {
        if (!_tokenWarningSentToday)
        {
            await _telegram.SendWarningAsync(
                "Upstox access token expired/rejected (401). Please generate a new token and update it in appsettings.json " +
                "(or your configured secret store) -- the bot will pick it up automatically without a restart.", ct);
            _tokenWarningSentToday = true;
        }

        _logger.LogWarning("Waiting for a fresh Upstox access token to be configured...");
        await Task.Delay(TimeSpan.FromMinutes(2), ct);
    }

    private async Task SafeAsync(Func<CancellationToken, Task> action, string operationName)
    {
        try
        {
            await action(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in fire-and-forget operation {Operation}", operationName);
        }
    }
}