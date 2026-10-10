using AlgoWorker.Configuration;
using AlgoWorker.Hubs;
using AlgoWorker.Models;
using AlgoWorker.Services.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using SharedModels = AlgoData.Models;

namespace AlgoWorker.Services;

/// <summary>
/// Virtual position lifecycle for the Previous-Day High/Low Breakout strategy.
/// Deliberately simple compared to VirtualPositionTracker (the EMA strategy's
/// tracker): one leg only (current-week ATM), and the ONLY exit rule is a
/// hard intraday cutoff time (design doc addendum: "same din 3:15-3:20 PM tak
/// force-exit, kal carry nahi karni") -- no ADX/technical exit logic here.
/// </summary>
public sealed class BreakoutPositionTracker
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
    private readonly ILogger<BreakoutPositionTracker> _logger;

    private BreakoutPosition? _active;

    // Daily trade cap (BreakoutMaxTradesPerDay). Loaded from the DB on the first signal of a day so a Worker restart doesn't reset it.
    private DateOnly _tradeCountDay;
    private int _tradesOpenedToday;

    public BreakoutPositionTracker(
        OptionInstrumentResolver instrumentResolver,
        UpstoxRestClient restClient,
        UpstoxWebSocketClient webSocketClient,
        TelegramAlertService telegram,
        IOptions<StrategyOptions> options,
        IPositionRepository positionRepository,
        ISignalLogRepository signalLogRepository,
        IHubContext<PositionHub> hub,
        IClock clock,
        ILogger<BreakoutPositionTracker> logger)
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

    public async Task OnBreakoutConfirmedAsync(BreakoutSignal signal, CancellationToken ct)
    {
        if (_active is { IsOpen: true })
        {
            _logger.LogInformation("A breakout position is already open -- ignoring new {Direction} breakout signal.", signal.Direction);
            await LogSignalAsync(signal, positionOpened: false, positionId: null, skipReason: "A breakout position is already open", ct);
            return;
        }

        var today = DateOnly.FromDateTime(signal.ConfirmedAtCandleTime.Date);

        if (_tradeCountDay != today)
        {
            _tradeCountDay = today;
            _tradesOpenedToday = await _positionRepository.CountBreakoutTradesOpenedOnAsync(today, ct);
        }
        if (_tradesOpenedToday >= _options.BreakoutMaxTradesPerDay)
        {
            _logger.LogInformation("Daily ORB trade limit ({Max}) reached -- ignoring {Direction} breakout.", _options.BreakoutMaxTradesPerDay, signal.Direction);
            await LogSignalAsync(signal, positionOpened: false, positionId: null, skipReason: $"Daily limit of {_options.BreakoutMaxTradesPerDay} ORB trades reached", ct);
            return;
        }

        var optionType = signal.Direction == BreakoutDirection.Up ? OptionType.Call : OptionType.Put;
        var instrument = await _instrumentResolver.ResolveCurrentWeekAtmAsync(signal.NiftySpotAtConfirmation, optionType, today, ct);
        if (instrument is null)
        {
            await _telegram.SendWarningAsync($"Breakout {signal.Direction} confirmed but could not resolve an ATM option instrument. No position opened.", ct);
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
            _logger.LogError(ex, "Failed to fetch entry LTP for breakout instrument {Instrument} -- position not opened.", instrument.InstrumentKey);
            await _telegram.SendWarningAsync($"Breakout confirmed but could not fetch entry price for {instrument.TradingSymbol}. Position not opened.", ct);
            await LogSignalAsync(signal, positionOpened: false, positionId: null, skipReason: "Entry LTP fetch failed", ct);
            return;
        }

        // Initial SL from the shared 3-phase "Final Updated Trailing Rule" (see TrailingStopCalculator.cs).
        var initialStopLoss = TrailingStopCalculator.ComputeStopLoss(entryPremium, entryPremium, _options);

        _active = new BreakoutPosition
        {
            Signal = signal,
            Instrument = instrument,
            LotSize = _options.LotSize,
            EntryPremium = entryPremium,
            OpenedAt = DateTimeOffset.Now,
            LastKnownPremium = entryPremium,
            LastUpdateSentAt = DateTimeOffset.Now,
            PeakPremium = entryPremium,
            CurrentStopLossPremium = initialStopLoss
        };

        // >>> NEW (Task 3): persist, then use the DB-assigned Id as this
        // position's identity going forward (Command API, SignalR, snapshot).
        // -1 (non-fatal, see PositionRepository) just means it won't show up
        // on the Dashboard until the next successful write.
        var dbId = await _positionRepository.SaveOpenedAsync(ToEntity(_active, entryPremium), ct);
        _active.Id = dbId;
        _tradesOpenedToday++;
        await LogSignalAsync(signal, positionOpened: dbId >= 0, positionId: dbId >= 0 ? dbId : null,
            skipReason: dbId < 0 ? "Position DB write failed (see logs)" : null, ct);

        await _webSocketClient.SubscribeAsync(instrument.InstrumentKey, ct);
        await _telegram.SendBreakoutEntryAlertAsync(signal, instrument, entryPremium, _options.LotSize, initialStopLoss, ct);
        await BroadcastAsync(_active, "Open", null, ct);

        _logger.LogInformation("Virtual breakout {Direction} position opened: {Symbol} @ {Premium}",
            signal.Direction, instrument.TradingSymbol, entryPremium);
    }

    /// <summary>
    /// Checks the stop-loss / trailing stop-loss rule on EVERY tick (not just
    /// candle close), so a fast intraday move against the position exits
    /// immediately rather than waiting up to 5 minutes. Uses the shared
    /// 3-phase "Final Updated Trailing Rule" (see TrailingStopCalculator.cs).
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
        position.TrailingActive = TrailingStopCalculator.IsTrailing(position.EntryPremium, position.PeakPremium, _options);

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

    /// <summary>Call once per closed 5-min candle -- checks the hard intraday cutoff and sends periodic P&amp;L updates.</summary>
    public async Task OnCandleClosedAsync(DateTimeOffset candleTime, CancellationToken ct)
    {
        if (_active is not { IsOpen: true } position) return;

        var cutoff = TimeOnly.Parse(_options.BreakoutForceExitTime);
        var candleTimeOfDay = TimeOnly.FromDateTime(candleTime.DateTime);

        if (candleTimeOfDay >= cutoff)
        {
            await CloseAsync(position, $"Intraday cutoff reached ({_options.BreakoutForceExitTime}) -- breakout trades are never carried overnight", ct);
            return;
        }

        if (DateTimeOffset.Now - position.LastUpdateSentAt >= TimeSpan.FromMinutes(_options.PositionUpdateIntervalMinutes))
        {
            await _telegram.SendBreakoutPositionUpdateAsync(position, ct);
            await _positionRepository.UpdateLivePremiumAsync(position.Id, position.LastKnownPremium, DateTimeOffset.Now, ct);
            await BroadcastAsync(position, "Open", null, ct);
            position.LastUpdateSentAt = DateTimeOffset.Now;
        }
    }

    private async Task CloseAsync(BreakoutPosition position, string reason, CancellationToken ct)
    {
        position.IsOpen = false;
        var exitTime = DateTimeOffset.Now;
        await _telegram.SendBreakoutExitAlertAsync(position, reason, ct);
        await _webSocketClient.UnsubscribeAsync(position.Instrument.InstrumentKey, ct);
        await _positionRepository.CloseAsync(position.Id, position.LastKnownPremium, exitTime, reason, position.PnlRupees, position.PnlPercent, ct);
        await BroadcastAsync(position, "Closed", reason, ct);

        _logger.LogInformation("Virtual breakout position closed. Reason: {Reason}. P&L: {Pnl:N2} ({PnlPct:N1}%)",
            reason, position.PnlRupees, position.PnlPercent);
    }

    /// <summary>Safety-net force close, e.g. if the process is shutting down for the day.</summary>
    public async Task CloseForMarketCloseAsync(CancellationToken ct)
    {
        if (_active is { IsOpen: true } position)
            await CloseAsync(position, "Market closed for the day", ct);
    }

    // >>> NEW (Task 5): Command API support.

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

    private static PositionUpdateDto ToDto(BreakoutPosition p, string status, string? exitReason) => new(
        PositionId: p.Id,
        Strategy: "Breakout",
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
    private async Task BroadcastAsync(BreakoutPosition position, string status, string? exitReason, CancellationToken ct)
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

    private SharedModels.PositionEntity ToEntity(BreakoutPosition position, decimal entryPremium) => new()
    {
        Strategy = SharedModels.StrategyType.Breakout,
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
        BrokenLevel = position.Signal.BrokenLevel,
        StopLossPremium = position.CurrentStopLossPremium,
        Status = SharedModels.PositionStatus.Open,
        LastKnownPremium = entryPremium,
        LastUpdateTime = position.OpenedAt,
        CreatedAt = _clock.Now
    };

    private async Task LogSignalAsync(BreakoutSignal signal, bool positionOpened, int? positionId, string? skipReason, CancellationToken ct)
    {
        var entry = new SharedModels.SignalLogEntity
        {
            Strategy = SharedModels.StrategyType.Breakout,
            Direction = signal.Direction.ToString(),
            SignalTime = signal.ConfirmedAtCandleTime,
            SpotPrice = signal.NiftySpotAtConfirmation,
            BrokenLevel = signal.BrokenLevel,
            IsCatchUp = false,
            PositionOpened = positionOpened,
            PositionId = positionId,
            SkipReason = skipReason,
            CreatedAt = _clock.Now
        };
        await _signalLogRepository.LogAsync(entry, ct);
    }
}