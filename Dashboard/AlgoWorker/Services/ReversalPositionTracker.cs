using AlgoWorker.Configuration;
using AlgoWorker.Hubs;
using AlgoWorker.Models;
using AlgoWorker.Services.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using SharedModels = AlgoData.Models;

namespace AlgoWorker.Services;

/// <summary>
/// Virtual position lifecycle for the Upside Reversal strategy. One position at a time (buy ATM CE, current week).
/// Exits are driven by NIFTY SPOT levels from the setup:
///   spot &lt;= SL            -> stop-loss exit
///   spot &gt;= T1            -> SL moves to cost (the entry spot); position stays open
///   spot &gt;= T2            -> target exit
///   force-exit time        -> intraday cutoff
/// Risk rules: max trades per day, and no more trades after N consecutive losses.
/// </summary>
public sealed class ReversalPositionTracker
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
    private readonly ILogger<ReversalPositionTracker> _logger;

    private ReversalPosition? _active;

    public ReversalPositionTracker(
        OptionInstrumentResolver instrumentResolver,
        UpstoxRestClient restClient,
        UpstoxWebSocketClient webSocketClient,
        TelegramAlertService telegram,
        IOptions<StrategyOptions> options,
        IPositionRepository positionRepository,
        ISignalLogRepository signalLogRepository,
        IHubContext<PositionHub> hub,
        IClock clock,
        ILogger<ReversalPositionTracker> logger)
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

    public async Task OnReversalConfirmedAsync(ReversalSignal signal, CancellationToken ct)
    {
        if (!_options.ReversalStrategyEnabled) return;

        if (_active is { IsOpen: true })
        {
            await LogSignalAsync(signal, false, null, "A reversal position is already open", ct);
            return;
        }

        var today = DateOnly.FromDateTime(signal.ConfirmedAtCandleTime.Date);
        var (opened, consecutiveLosses) = await _positionRepository.GetReversalDayStatsAsync(today, ct);
        if (opened >= _options.ReversalMaxTradesPerDay)
        {
            await LogSignalAsync(signal, false, null, $"Daily limit of {_options.ReversalMaxTradesPerDay} reversal trades reached", ct);
            return;
        }
        if (consecutiveLosses >= _options.ReversalMaxConsecutiveLosses)
        {
            await LogSignalAsync(signal, false, null, $"{consecutiveLosses} consecutive losses today -- reversal trading stopped for the day", ct);
            return;
        }

        var instrument = await _instrumentResolver.ResolveCurrentWeekAtmAsync(signal.EntrySpot, OptionType.Call, today, ct);
        if (instrument is null)
        {
            await _telegram.SendWarningAsync($"Reversal ({signal.SetupName}) confirmed but could not resolve an ATM CE instrument. No position opened.", ct);
            await LogSignalAsync(signal, false, null, "Could not resolve an ATM option instrument", ct);
            return;
        }

        decimal entryPremium;
        try
        {
            entryPremium = await _restClient.GetLastTradedPriceAsync(instrument.InstrumentKey, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch entry LTP for reversal instrument {Instrument}.", instrument.InstrumentKey);
            await _telegram.SendWarningAsync($"Reversal confirmed but could not fetch entry price for {instrument.TradingSymbol}. Position not opened.", ct);
            await LogSignalAsync(signal, false, null, "Entry LTP fetch failed", ct);
            return;
        }

        _active = new ReversalPosition
        {
            Signal = signal,
            Instrument = instrument,
            LotSize = _options.LotSize,
            EntryPremium = entryPremium,
            OpenedAt = DateTimeOffset.Now,
            LastKnownPremium = entryPremium,
            LastUpdateSentAt = DateTimeOffset.Now,
            StopLossSpot = signal.StopLossSpot,
            LastSpot = signal.EntrySpot
        };

        var dbId = await _positionRepository.SaveOpenedAsync(ToEntity(_active, entryPremium), ct);
        _active.Id = dbId;
        await LogSignalAsync(signal, dbId >= 0, dbId >= 0 ? dbId : null, dbId < 0 ? "Position DB write failed (see logs)" : null, ct);

        await _webSocketClient.SubscribeAsync(instrument.InstrumentKey, ct);
        await _telegram.SendReversalEntryAlertAsync(signal, instrument, entryPremium, _options.LotSize, ct);
        await BroadcastAsync(_active, "Open", null, ct);

        _logger.LogInformation("Virtual reversal ({Setup}) position opened: {Symbol} @ {Premium}", signal.Setup, instrument.TradingSymbol, entryPremium);
    }

    /// <summary>Nifty SPOT tick: this is where SL / T1 / T2 are evaluated (every tick).</summary>
    public async Task OnSpotTick(decimal spot, CancellationToken ct)
    {
        if (_active is not { IsOpen: true } position) return;
        position.LastSpot = spot;

        if (spot <= position.StopLossSpot)
        {
            var reason = position.Target1Hit
                ? $"Stop-loss at cost hit (Nifty {position.StopLossSpot:N2}) after T1"
                : $"Stop-loss hit (Nifty {position.StopLossSpot:N2})";
            await CloseAsync(position, reason, ct);
            return;
        }

        if (spot >= position.Signal.Target2Spot)
        {
            await CloseAsync(position, $"Target 2 hit (Nifty {position.Signal.Target2Spot:N2})", ct);
            return;
        }

        if (!position.Target1Hit && spot >= position.Signal.Target1Spot)
        {
            position.Target1Hit = true;
            position.StopLossSpot = Math.Max(position.StopLossSpot, position.Signal.EntrySpot);
            await _positionRepository.UpdateStopLossAsync(position.Id, null, position.StopLossSpot, ct);   // persist SL moved to cost
            await _telegram.SendReversalTarget1Async(position, ct);
            await BroadcastAsync(position, "Open", null, ct);
        }
    }

    /// <summary>Option tick: keeps the premium (and so the P&amp;L) fresh; throttled Dashboard push.</summary>
    public async Task OnOptionTick(MarketTick tick, CancellationToken ct)
    {
        if (_active is not { IsOpen: true } position || position.Instrument.InstrumentKey != tick.InstrumentKey)
            return;

        position.LastKnownPremium = tick.LastTradedPrice;

        if (DateTimeOffset.Now - position.LastBroadcastAt >= TimeSpan.FromSeconds(1))
        {
            position.LastBroadcastAt = DateTimeOffset.Now;
            await BroadcastAsync(position, "Open", null, ct);
        }
    }

    public async Task OnCandleClosedAsync(DateTimeOffset candleTime, CancellationToken ct)
    {
        if (_active is not { IsOpen: true } position) return;

        var cutoff = TimeOnly.Parse(_options.ReversalForceExitTime);
        if (TimeOnly.FromDateTime(candleTime.DateTime) >= cutoff)
        {
            await CloseAsync(position, $"Intraday cutoff reached ({_options.ReversalForceExitTime}) -- reversal trades are never carried overnight", ct);
            return;
        }

        if (DateTimeOffset.Now - position.LastUpdateSentAt >= TimeSpan.FromMinutes(_options.PositionUpdateIntervalMinutes))
        {
            await _telegram.SendReversalPositionUpdateAsync(position, ct);
            await _positionRepository.UpdateLivePremiumAsync(position.Id, position.LastKnownPremium, DateTimeOffset.Now, ct);
            await BroadcastAsync(position, "Open", null, ct);
            position.LastUpdateSentAt = DateTimeOffset.Now;
        }
    }

    private async Task CloseAsync(ReversalPosition position, string reason, CancellationToken ct)
    {
        if (!position.IsOpen) return;
        position.IsOpen = false;
        var exitTime = DateTimeOffset.Now;
        await _telegram.SendReversalExitAlertAsync(position, reason, ct);
        await _webSocketClient.UnsubscribeAsync(position.Instrument.InstrumentKey, ct);
        await _positionRepository.CloseAsync(position.Id, position.LastKnownPremium, exitTime, reason, position.PnlRupees, position.PnlPercent, ct);
        await BroadcastAsync(position, "Closed", reason, ct);

        _logger.LogInformation("Virtual reversal position closed. Reason: {Reason}. P&L: {Pnl:N2} ({PnlPct:N1}%)", reason, position.PnlRupees, position.PnlPercent);
    }

    public async Task CloseForMarketCloseAsync(CancellationToken ct)
    {
        if (_active is { IsOpen: true } position)
            await CloseAsync(position, "Market closed for the day", ct);
    }

    public async Task<bool> ForceCloseAsync(int id, CancellationToken ct)
    {
        if (_active is not { IsOpen: true } position || position.Id != id)
            return false;

        await CloseAsync(position, "Manually closed from Dashboard", ct);
        return true;
    }

    public IEnumerable<PositionUpdateDto> GetOpenPositionsSnapshot()
    {
        if (_active is not { IsOpen: true } position)
            return Enumerable.Empty<PositionUpdateDto>();
        return new[] { ToDto(position, "Open", null) };
    }

    private static PositionUpdateDto ToDto(ReversalPosition p, string status, string? exitReason) => new(
        PositionId: p.Id,
        Strategy: "Reversal",
        Direction: "Bullish",
        Leg: null,
        TradingSymbol: p.Instrument.TradingSymbol,
        Strike: p.Instrument.Strike,
        OptionType: "CE",
        Expiry: p.Instrument.Expiry,
        EntryPremium: p.EntryPremium,
        EntryTime: p.OpenedAt,
        CurrentPremium: p.LastKnownPremium,
        PnlRupees: p.PnlRupees,
        PnlPercent: p.PnlPercent,
        Status: status,
        ExitReason: exitReason,
        StopLossPremium: 0m,                   // Reversal SL is a Nifty spot level, not a premium
        StopLossSpot: p.StopLossSpot,
        StopLossTrailing: p.Target1Hit);

    private async Task BroadcastAsync(ReversalPosition position, string status, string? exitReason, CancellationToken ct)
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

    private SharedModels.PositionEntity ToEntity(ReversalPosition position, decimal entryPremium) => new()
    {
        Strategy = SharedModels.StrategyType.Reversal,
        Direction = "Bullish",
        Leg = null,
        InstrumentKey = position.Instrument.InstrumentKey,
        TradingSymbol = position.Instrument.TradingSymbol,
        Strike = position.Instrument.Strike,
        OptionType = SharedModels.OptionType.CE,
        Expiry = position.Instrument.Expiry,
        LotSize = position.LotSize,
        SpotAtEntry = position.Signal.EntrySpot,
        EntryPremium = entryPremium,
        EntryTime = position.OpenedAt,
        BrokenLevel = position.Signal.SupportLevel,
        StopLossSpot = position.StopLossSpot,
        Status = SharedModels.PositionStatus.Open,
        LastKnownPremium = entryPremium,
        LastUpdateTime = position.OpenedAt,
        CreatedAt = _clock.Now
    };

    private async Task LogSignalAsync(ReversalSignal signal, bool positionOpened, int? positionId, string? skipReason, CancellationToken ct)
    {
        var entry = new SharedModels.SignalLogEntity
        {
            Strategy = SharedModels.StrategyType.Reversal,
            Direction = "Bullish",
            SignalTime = signal.ConfirmedAtCandleTime,
            SpotPrice = signal.EntrySpot,
            BrokenLevel = signal.SupportLevel,
            IsCatchUp = false,
            PositionOpened = positionOpened,
            PositionId = positionId,
            SkipReason = skipReason,
            CreatedAt = _clock.Now
        };
        await _signalLogRepository.LogAsync(entry, ct);
    }
}
