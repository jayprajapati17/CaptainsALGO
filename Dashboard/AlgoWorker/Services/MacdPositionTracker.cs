using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AlgoWorker.Configuration;
using AlgoWorker.Hubs;
using AlgoWorker.Models;
using AlgoWorker.Services.Indicators;
using AlgoWorker.Services.Persistence;
using SharedModels = AlgoData.Models;

namespace AlgoWorker.Services;

/// <summary>
/// Virtual position lifecycle for the 3-Minute MACD strategy. One leg only
/// (current-week ATM), no multi-candle confirmation on entry. Exit rules, in
/// the order they're checked, per the doc:
///   1) Dynamic step-trailing stop-loss (checked every tick -- see OnOptionTick).
///   2) Opposite MACD crossover (checked every closed 3-min candle).
///   3) Hard intraday cutoff (no overnight carry, same as the breakout strategy).
/// </summary>
public sealed class MacdPositionTracker
{
    private readonly OptionInstrumentResolver _instrumentResolver;
    private readonly UpstoxRestClient _restClient;
    private readonly UpstoxWebSocketClient _webSocketClient;
    private readonly TelegramAlertService _telegram;
    private readonly StrategyOptions _options;
    private readonly IPositionRepository _positionRepository;
    private readonly ISignalLogRepository _signalLogRepository;
    private readonly IHubContext<PositionHub> _hub;
    private readonly IClock _clock;
    private readonly ILogger<MacdPositionTracker> _logger;

    private MacdPosition? _active;

    public MacdPositionTracker(
        OptionInstrumentResolver instrumentResolver,
        UpstoxRestClient restClient,
        UpstoxWebSocketClient webSocketClient,
        TelegramAlertService telegram,
        IOptions<StrategyOptions> options,
        IPositionRepository positionRepository,
        ISignalLogRepository signalLogRepository,
        IHubContext<PositionHub> hub,
        IClock clock,
        ILogger<MacdPositionTracker> logger)
    {
        _instrumentResolver = instrumentResolver;
        _restClient = restClient;
        _webSocketClient = webSocketClient;
        _telegram = telegram;
        _options = options.Value;
        _positionRepository = positionRepository;
        _signalLogRepository = signalLogRepository;
        _hub = hub;
        _clock = clock;
        _logger = logger;
    }

    public async Task OnMacdConfirmedAsync(MacdSignal signal, CancellationToken ct)
    {
        if (!_options.MacdStrategyEnabled)
            return;

        if (_active is { IsOpen: true })
        {
            _logger.LogInformation("A MACD position is already open -- ignoring new {Direction} MACD signal.", signal.Direction);
            await LogSignalAsync(signal, positionOpened: false, positionId: null, skipReason: "A MACD position is already open", ct);
            return;
        }

        var optionType = signal.Direction == MacdDirection.Bullish ? OptionType.Call : OptionType.Put;
        var today = DateOnly.FromDateTime(signal.ConfirmedAtCandleTime.Date);

        var instrument = await _instrumentResolver.ResolveCurrentWeekAtmAsync(signal.NiftySpotAtConfirmation, optionType, today, ct);
        if (instrument is null)
        {
            await _telegram.SendWarningAsync($"MACD {signal.Direction} confirmed but could not resolve an ATM option instrument. No position opened.", ct);
            await LogSignalAsync(signal, positionOpened: false, positionId: null, skipReason: "Could not resolve an ATM option instrument", ct);
            return;
        }

        decimal entryPremium;
        try
        {
            entryPremium = await _restClient.GetLastTradedPriceAsync(instrument.InstrumentKey, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch entry LTP for MACD instrument {Instrument} -- position not opened.", instrument.InstrumentKey);
            await _telegram.SendWarningAsync($"MACD confirmed but could not fetch entry price for {instrument.TradingSymbol}. Position not opened.", ct);
            await LogSignalAsync(signal, positionOpened: false, positionId: null, skipReason: "Entry LTP fetch failed", ct);
            return;
        }

        // Initial SL from the shared 3-phase "Final Updated Trailing Rule" (see TrailingStopCalculator.cs).
        var initialStopLoss = TrailingStopCalculator.ComputeStopLoss(entryPremium, entryPremium, _options);

        _active = new MacdPosition
        {
            Signal = signal,
            Instrument = instrument,
            LotSize = _options.MacdLotSize,
            EntryPremium = entryPremium,
            OpenedAt = DateTimeOffset.Now,
            LastKnownPremium = entryPremium,
            LastUpdateSentAt = DateTimeOffset.Now,
            PeakPremium = entryPremium,
            CurrentStopLossPremium = initialStopLoss
        };

        var dbId = await _positionRepository.SaveOpenedAsync(ToEntity(_active, entryPremium), ct);
        _active.Id = dbId;
        await LogSignalAsync(signal, positionOpened: dbId >= 0, positionId: dbId >= 0 ? dbId : null,
            skipReason: dbId < 0 ? "Position DB write failed (see logs)" : null, ct);

        await _webSocketClient.SubscribeAsync(instrument.InstrumentKey, ct);
        await _telegram.SendMacdEntryAlertAsync(signal, instrument, entryPremium, _options.MacdLotSize, initialStopLoss, ct);
        await BroadcastAsync(_active, "Open", null, ct);

        _logger.LogInformation("Virtual MACD {Direction} position opened: {Symbol} @ {Premium}",
            signal.Direction, instrument.TradingSymbol, entryPremium);
    }

    /// <summary>
    /// Checked on every tick: updates PeakPremium, recomputes the stop-loss via
    /// the shared 3-phase "Final Updated Trailing Rule" (see
    /// TrailingStopCalculator.cs), sends a one-time "target hit" informational
    /// alert (does not exit), and exits if the current stop is breached.
    /// </summary>
    public async Task OnOptionTick(MarketTick tick, CancellationToken ct)
    {
        if (_active is not { IsOpen: true } position || position.Instrument.InstrumentKey != tick.InstrumentKey)
            return;

        position.LastKnownPremium = tick.LastTradedPrice;

        if (tick.LastTradedPrice > position.PeakPremium)
            position.PeakPremium = tick.LastTradedPrice;

        var newStopLoss = TrailingStopCalculator.ComputeStopLoss(position.EntryPremium, position.PeakPremium, _options);
        if (newStopLoss != position.CurrentStopLossPremium)
        {
            position.CurrentStopLossPremium = newStopLoss;
            await _positionRepository.UpdateStopLossAsync(position.Id, newStopLoss, null, ct);   // persist the trailing SL
        }

        var pointsGained = position.PeakPremium - position.EntryPremium;
        if (!position.TargetAlertSent && pointsGained >= (decimal)_options.TrailingTargetPoints)
        {
            position.TargetAlertSent = true;
            await _telegram.SendMacdTargetHitAlertAsync(position, ct);
        }

        if (tick.LastTradedPrice <= position.CurrentStopLossPremium)
        {
            var reason = $"Stop-loss hit at ₹{position.CurrentStopLossPremium:N2} (peak ₹{position.PeakPremium:N2})";
            await CloseAsync(position, reason, ct);
            return;
        }

        // >>> NEW (Task 8): live premium/P&L push to the Dashboard, throttled to ~1/sec.
        if (DateTimeOffset.Now - position.LastBroadcastAt >= TimeSpan.FromSeconds(1))
        {
            position.LastBroadcastAt = DateTimeOffset.Now;
            await BroadcastAsync(position, "Open", null, ct);
        }
    }

    /// <summary>
    /// Call once per closed 3-min candle (AFTER MacdEngine.OnCandleClosed for
    /// this same candle, so engine.Latest is the current snapshot). Checks the
    /// opposite-crossover exit and the hard intraday cutoff, and sends periodic updates.
    /// </summary>
    public async Task OnCandleClosedAsync(MacdEngine engine, MacdSnapshot current, CancellationToken ct)
    {
        if (_active is not { IsOpen: true } position) return;

        var previous = engine.NCandlesAgo(1);
        if (previous is not null && MacdSignalEngine.IsOppositeCrossover(position.Signal.Direction, previous, current))
        {
            await CloseAsync(position, $"Opposite MACD crossover at {current.CandleTime:hh:mm tt} (MACD {current.MacdLine:F2}, Signal {current.SignalLine:F2})", ct);
            return;
        }

        var cutoff = TimeOnly.Parse(_options.MacdForceExitTime);
        var candleTimeOfDay = TimeOnly.FromDateTime(current.CandleTime.DateTime);
        if (candleTimeOfDay >= cutoff)
        {
            await CloseAsync(position, $"Intraday cutoff reached ({_options.MacdForceExitTime}) -- MACD trades are never carried overnight", ct);
            return;
        }

        if (DateTimeOffset.Now - position.LastUpdateSentAt >= TimeSpan.FromMinutes(_options.PositionUpdateIntervalMinutes))
        {
            await _telegram.SendMacdPositionUpdateAsync(position, ct);
            await _positionRepository.UpdateLivePremiumAsync(position.Id, position.LastKnownPremium, DateTimeOffset.Now, ct);
            await BroadcastAsync(position, "Open", null, ct);
            position.LastUpdateSentAt = DateTimeOffset.Now;
        }
    }

    private async Task CloseAsync(MacdPosition position, string reason, CancellationToken ct)
    {
        position.IsOpen = false;
        var exitTime = DateTimeOffset.Now;
        await _telegram.SendMacdExitAlertAsync(position, reason, ct);
        await _webSocketClient.UnsubscribeAsync(position.Instrument.InstrumentKey, ct);
        await _positionRepository.CloseAsync(position.Id, position.LastKnownPremium, exitTime, reason, position.PnlRupees, position.PnlPercent, ct);
        await BroadcastAsync(position, "Closed", reason, ct);

        _logger.LogInformation("Virtual MACD position closed. Reason: {Reason}. P&L: {Pnl:N2} ({PnlPct:N1}%)",
            reason, position.PnlRupees, position.PnlPercent);
    }

    /// <summary>Safety-net force close, e.g. if the process is shutting down for the day.</summary>
    public async Task CloseForMarketCloseAsync(CancellationToken ct)
    {
        if (_active is { IsOpen: true } position)
            await CloseAsync(position, "Market closed for the day", ct);
    }

    // >>> NEW (Task 5): Command API support (brings MACD in line with the other two strategies).

    /// <summary>Manual close from the Dashboard's "Close Position" button (POST /api/positions/{id}/close). Returns false if no OPEN position with this Id exists here.</summary>
    public async Task<bool> ForceCloseAsync(int id, CancellationToken ct)
    {
        if (_active is not { IsOpen: true } position || position.Id != id)
            return false;

        await CloseAsync(position, "Manually closed from Dashboard", ct);
        return true;
    }

    /// <summary>Snapshot of the currently OPEN position (if any) for GET /api/positions/open.</summary>
    public IEnumerable<PositionUpdateDto> GetOpenPositionsSnapshot()
    {
        if (_active is not { IsOpen: true } position)
            return Enumerable.Empty<PositionUpdateDto>();

        return new[] { ToDto(position, "Open", null) };
    }

    // >>> NEW (Task 3 + Task 4): DB persistence + SignalR broadcast helpers.

    private static PositionUpdateDto ToDto(MacdPosition p, string status, string? exitReason) => new(
        PositionId: p.Id,
        Strategy: "Macd",
        Direction: p.Signal.Direction.ToString(),
        Leg: null,
        TradingSymbol: p.Instrument.TradingSymbol,
        Strike: p.Instrument.Strike,
        OptionType: p.Instrument.Type == OptionType.Call ? "CE" : "PE",
        Expiry: p.Instrument.Expiry,
        EntryPremium: p.EntryPremium,
        EntryTime: p.OpenedAt,
        CurrentPremium: p.LastKnownPremium,
        PnlRupees: p.PnlRupees,
        PnlPercent: p.PnlPercent,
        Status: status,
        ExitReason: exitReason,
        StopLossPremium: p.CurrentStopLossPremium);

    /// <summary>Broadcasts on PositionHub's "PositionChanged" event -- the Dashboard's Live page (Task 8) listens for this.</summary>
    private async Task BroadcastAsync(MacdPosition position, string status, string? exitReason, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync("PositionChanged", ToDto(position, status, exitReason), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast PositionChanged for PositionId {Id}.", position.Id);
        }
    }

    private SharedModels.PositionEntity ToEntity(MacdPosition position, decimal entryPremium) => new()
    {
        Strategy = SharedModels.StrategyType.Macd,
        Direction = position.Signal.Direction.ToString(),
        Leg = null,
        InstrumentKey = position.Instrument.InstrumentKey,
        TradingSymbol = position.Instrument.TradingSymbol,
        Strike = position.Instrument.Strike,
        OptionType = position.Instrument.Type == OptionType.Call ? SharedModels.OptionType.CE : SharedModels.OptionType.PE,
        Expiry = position.Instrument.Expiry,
        LotSize = position.LotSize,
        SpotAtEntry = position.Signal.NiftySpotAtConfirmation,
        EntryPremium = entryPremium,
        EntryTime = position.OpenedAt,
        StopLossPremium = position.CurrentStopLossPremium,
        Status = SharedModels.PositionStatus.Open,
        LastKnownPremium = entryPremium,
        LastUpdateTime = position.OpenedAt,
        CreatedAt = _clock.Now
    };

    private async Task LogSignalAsync(MacdSignal signal, bool positionOpened, int? positionId, string? skipReason, CancellationToken ct)
    {
        var entry = new SharedModels.SignalLogEntity
        {
            Strategy = SharedModels.StrategyType.Macd,
            Direction = signal.Direction.ToString(),
            SignalTime = signal.ConfirmedAtCandleTime,
            SpotPrice = signal.NiftySpotAtConfirmation,
            IsCatchUp = false,
            PositionOpened = positionOpened,
            PositionId = positionId,
            SkipReason = skipReason,
            CreatedAt = _clock.Now
        };
        await _signalLogRepository.LogAsync(entry, ct);
    }
}