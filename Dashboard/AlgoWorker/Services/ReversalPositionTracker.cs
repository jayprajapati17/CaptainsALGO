using AlgoWorker.Configuration;
using AlgoWorker.Hubs;
using AlgoWorker.Models;
using AlgoWorker.Services.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using SharedModels = AlgoData.Models;

namespace AlgoWorker.Services;

/// <summary>
/// Virtual position lifecycle for the Liquidity Sweep (CE) strategy. One position at a time (buy ATM CE, current week).
///   stop-loss: the previous (red) candle's low, a NIFTY SPOT level -- spot &lt;= SL closes the trade, always.
///   EMA 9 &gt;= EMA 21 (or price above EMA 21) -> TrendHold: no target; the app's existing step-trailing stop-loss
///                                                (premium based, see TrailingStop) protects the profit.
///   EMA 9 &lt;  EMA 21 -> Ema21Target: hold until Nifty reaches (touches / nears) EMA 21. With "wait for candle close" a 5-min
///                      candle closing ABOVE EMA 21 turns it into TrendHold; otherwise the trade exits at the close.
///   force-exit time  -> intraday cutoff
/// Risk rules: max trades per day, and no more trades after N consecutive losses.
/// </summary>
public sealed class ReversalPositionTracker
{
    private readonly OptionInstrumentResolver _instrumentResolver;
    private readonly UpstoxRestClient _restClient;
    private readonly UpstoxWebSocketClient _webSocketClient;
    private readonly TelegramAlertService _telegram;
    private readonly IReversalSettings _options;
    private readonly EmaTarget _target;   // index name / Underlying tag / Upstox key (shared record with the EMA tracker)
    private readonly IPositionRepository _positionRepository;
    private readonly ISignalLogRepository _signalLogRepository;
    private readonly IHubContext<PositionHub> _hub;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    private ReversalPosition? _active;

    public ReversalPositionTracker(
        OptionInstrumentResolver instrumentResolver,
        UpstoxRestClient restClient,
        UpstoxWebSocketClient webSocketClient,
        TelegramAlertService telegram,
        IOptions<StrategyOptions> options,
        IOptions<UpstoxOptions> upstox,
        IPositionRepository positionRepository,
        ISignalLogRepository signalLogRepository,
        IHubContext<PositionHub> hub,
        IClock clock,
        ILogger<ReversalPositionTracker> logger)
        : this(instrumentResolver, restClient, webSocketClient, telegram, options.Value,
               new EmaTarget("Nifty", SharedModels.Underlyings.Nifty50, upstox.Value.NiftyInstrumentKey),
               positionRepository, signalLogRepository, hub, clock, logger)
    { }

    /// <summary>Generic form: the same Reversal position logic for any index.</summary>
    public ReversalPositionTracker(
        OptionInstrumentResolver instrumentResolver,
        UpstoxRestClient restClient,
        UpstoxWebSocketClient webSocketClient,
        TelegramAlertService telegram,
        IReversalSettings options,
        EmaTarget target,
        IPositionRepository positionRepository,
        ISignalLogRepository signalLogRepository,
        IHubContext<PositionHub> hub,
        IClock clock,
        ILogger logger)
    {
        _instrumentResolver = instrumentResolver;
        _restClient = restClient;
        _webSocketClient = webSocketClient;
        _telegram = telegram;
        _options = options;
        _target = target;
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
            await LogSignalAsync(signal, false, null, "A liquidity-sweep position is already open", ct);
            return;
        }

        var today = DateOnly.FromDateTime(signal.ConfirmedAtCandleTime.Date);
        var (opened, consecutiveLosses) = await _positionRepository.GetReversalDayStatsAsync(today, ct, _target.Underlying);
        if (opened >= _options.ReversalMaxTradesPerDay)
        {
            await LogSignalAsync(signal, false, null, $"Daily limit of {_options.ReversalMaxTradesPerDay} liquidity-sweep trades reached", ct);
            return;
        }
        if (consecutiveLosses >= _options.ReversalMaxConsecutiveLosses)
        {
            await LogSignalAsync(signal, false, null, $"{consecutiveLosses} consecutive losses today -- liquidity-sweep trading stopped for the day", ct);
            return;
        }

        var instrument = await _instrumentResolver.ResolveCurrentAtmAsync(_target.UnderlyingKey, signal.EntrySpot, OptionType.Call, _options.StrikeStepPoints, today, ct);
        if (instrument is null)
        {
            await _telegram.SendWarningAsync($"{_target.Name} Liquidity sweep confirmed but could not resolve an ATM CE instrument. No position opened.", ct);
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
            _logger.LogError(ex, "Failed to fetch entry LTP for liquidity-sweep instrument {Instrument}.", instrument.InstrumentKey);
            await _telegram.SendWarningAsync($"Liquidity sweep confirmed but could not fetch entry price for {instrument.TradingSymbol}. Position not opened.", ct);
            await LogSignalAsync(signal, false, null, "Entry LTP fetch failed", ct);
            return;
        }

        _active = new ReversalPosition
        {
            Signal = signal,
            Instrument = instrument,
            LotSize = _options.LotSize,
            EntryPremium = entryPremium,
            OpenedAt = DateTimeOffset.UtcNow,
            LastKnownPremium = entryPremium,
            LastUpdateSentAt = DateTimeOffset.UtcNow,
            StopLossSpot = signal.StopLossSpot,
            LastSpot = signal.EntrySpot,
            Mode = signal.HoldWithTrailing ? ReversalHoldMode.TrendHold : ReversalHoldMode.Ema21Target,
            EmaShort = signal.EmaShort,
            EmaLong = signal.EmaLong,
            PeakPremium = entryPremium,
            EntryCandleTime = signal.ConfirmedAtCandleTime
        };

        var dbId = await _positionRepository.SaveOpenedAsync(ToEntity(_active, entryPremium), ct);
        _active.Id = dbId;
        await LogSignalAsync(signal, dbId >= 0, dbId >= 0 ? dbId : null, dbId < 0 ? "Position DB write failed (see logs)" : null, ct);

        await _webSocketClient.SubscribeAsync(instrument.InstrumentKey, ct);
        await _telegram.SendReversalEntryAlertAsync(signal, instrument, entryPremium, _options.LotSize, ct, _target.Name);
        await BroadcastAsync(_active, "Open", null, ct);

        _logger.LogInformation("Virtual liquidity-sweep position opened: {Symbol} @ {Premium} ({Mode}).", instrument.TradingSymbol, entryPremium, _active.Mode);
    }

    /// <summary>Index SPOT tick: the stop-loss and the "reached EMA 21" check are evaluated on every tick.</summary>
    public async Task OnSpotTick(decimal spot, CancellationToken ct)
    {
        if (_active is not { IsOpen: true } position) return;
        position.LastSpot = spot;

        if (spot <= position.StopLossSpot)
        {
            await CloseAsync(position, $"Stop-loss hit: {_target.Name} {spot:N2} reached the previous candle's low ({position.StopLossSpot:N2})", ct);
            return;
        }

        // EMA 9 below EMA 21: the trade is held until Nifty reaches (touches / nears) EMA 21.
        if (position.Mode == ReversalHoldMode.Ema21Target && !position.AtEma21Zone && position.EmaLong > 0
            && spot >= position.EmaLong - (decimal)_options.ReversalEmaTouchTolerancePoints)
        {
            if (!_options.ReversalWaitForCandleCloseAtEma)
            {
                await CloseAsync(position, $"Target: {_target.Name} {spot:N2} reached EMA {_options.ReversalEmaLongPeriod} ({position.EmaLong:N2})", ct);
                return;
            }

            position.AtEma21Zone = true;
            await _telegram.SendReversalNoteAsync(position,
                $"{_target.Name} <code>{spot:N2}</code> reached EMA {_options.ReversalEmaLongPeriod} <code>{position.EmaLong:N2}</code>.\n" +
                "Waiting for the 5-min candle close: a close above it keeps the trade (trailing stop-loss), otherwise it exits.", ct, _target.Name);
            await BroadcastAsync(position, "Open", null, ct);
        }
    }

    /// <summary>Option tick: keeps the premium (and so the P&amp;L) fresh and runs the existing trailing stop-loss in TrendHold.</summary>
    public async Task OnOptionTick(MarketTick tick, CancellationToken ct)
    {
        if (_active is not { IsOpen: true } position || position.Instrument.InstrumentKey != tick.InstrumentKey)
            return;

        position.LastKnownPremium = tick.LastTradedPrice;
        if (tick.LastTradedPrice > position.PeakPremium) position.PeakPremium = tick.LastTradedPrice;

        if (position.Mode == ReversalHoldMode.TrendHold)
        {
            // The app's existing step-trailing rule (premium points). It stays out of the way until the premium has gained one
            // step, so until then only the previous-candle-low stop-loss (a Nifty level) applies.
            var trail = TrailingParams.From(_options);
            if (TrailingStop.IsTrailing(position.EntryPremium, position.PeakPremium, trail))
            {
                var sl = TrailingStop.Compute(position.EntryPremium, position.PeakPremium, trail);
                if (sl > position.TrailingStopPremium)
                {
                    position.TrailingStopPremium = sl;
                    await _positionRepository.UpdateStopLossAsync(position.Id, sl, position.StopLossSpot, ct);
                }
            }

            if (position.TrailingActive && tick.LastTradedPrice <= position.TrailingStopPremium)
            {
                await CloseAsync(position, $"Trailing stop-loss hit (premium {position.TrailingStopPremium:N2})", ct);
                return;
            }
        }

        if (DateTimeOffset.UtcNow - position.LastBroadcastAt >= TimeSpan.FromSeconds(1))
        {
            position.LastBroadcastAt = DateTimeOffset.UtcNow;
            await BroadcastAsync(position, "Open", null, ct);
        }
    }

    /// <summary>Called after every closed 5-min candle with the EMA values the signal engine has just updated.</summary>
    public async Task OnCandleClosedAsync(DateTimeOffset candleTime, decimal candleClose, decimal emaShort, decimal emaLong, CancellationToken ct)
    {
        if (_active is not { IsOpen: true } position) return;

        var cutoff = TimeOfDay.Parse(_options.ReversalForceExitTime);
        if (TimeOfDay.Of(candleTime) >= cutoff)
        {
            await CloseAsync(position, $"Intraday cutoff reached ({_options.ReversalForceExitTime}) -- trades are never carried overnight", ct);
            return;
        }

        // The entry candle itself is not evaluated again; the rules below start from the next candle.
        if (candleTime > position.EntryCandleTime)
        {
            position.EmaShort = emaShort;
            position.EmaLong = emaLong;

            if (position.Mode == ReversalHoldMode.Ema21Target)
            {
                if (candleClose > emaLong || emaShort >= emaLong)
                {
                    // Price closed above EMA 21 (or EMA 9 crossed above it): no longer a short bounce -- hold long, trail the stop-loss.
                    position.Mode = ReversalHoldMode.TrendHold;
                    position.AtEma21Zone = false;
                    await _telegram.SendReversalNoteAsync(position,
                        $"{_target.Name} closed above EMA {_options.ReversalEmaLongPeriod} <code>{emaLong:N2}</code> (candle close <code>{candleClose:N2}</code>).\n" +
                        "Holding the long trade with the trailing stop-loss; no fixed target.", ct, _target.Name);
                    await BroadcastAsync(position, "Open", null, ct);
                }
                else if (position.AtEma21Zone)
                {
                    await CloseAsync(position, $"Target: {_target.Name} reached EMA {_options.ReversalEmaLongPeriod} ({emaLong:N2}) but the candle closed below it ({candleClose:N2})", ct);
                    return;
                }
            }
        }

        if (DateTimeOffset.UtcNow - position.LastUpdateSentAt >= TimeSpan.FromMinutes(_options.PositionUpdateIntervalMinutes))
        {
            await _telegram.SendReversalPositionUpdateAsync(position, ct, _target.Name);
            await _positionRepository.UpdateLivePremiumAsync(position.Id, position.LastKnownPremium, DateTimeOffset.UtcNow, ct);
            await BroadcastAsync(position, "Open", null, ct);
            position.LastUpdateSentAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task CloseAsync(ReversalPosition position, string reason, CancellationToken ct)
    {
        if (!position.IsOpen) return;
        position.IsOpen = false;
        var exitTime = DateTimeOffset.UtcNow;
        await _telegram.SendReversalExitAlertAsync(position, reason, ct, _target.Name);
        await _webSocketClient.UnsubscribeAsync(position.Instrument.InstrumentKey, ct);
        await _positionRepository.CloseAsync(position.Id, position.LastKnownPremium, exitTime, reason, position.PnlRupees, position.PnlPercent, ct);
        await BroadcastAsync(position, "Closed", reason, ct);

        _logger.LogInformation("Virtual liquidity-sweep position closed. Reason: {Reason}. P&L: {Pnl:N2} ({PnlPct:N1}%)", reason, position.PnlRupees, position.PnlPercent);
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

    private PositionUpdateDto ToDto(ReversalPosition p, string status, string? exitReason) => new(
        PositionId: p.Id,
        Strategy: "Reversal",   // stored/filtered under this key; shown as "Liquidity sweep (CE)"
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
        StopLossPremium: p.TrailingStopPremium,   // 0 until the trailing stop-loss has started; the Nifty-level stop-loss is StopLossSpot
        StopLossSpot: p.StopLossSpot,
        StopLossTrailing: p.TrailingActive,
        Underlying: _target.Underlying);

    private async Task BroadcastAsync(ReversalPosition position, string status, string? exitReason, CancellationToken ct)
    {
        await PositionBroadcast.PublishAsync(_hub, ToDto(position, status, exitReason), _logger, ct);
    }

    private SharedModels.PositionEntity ToEntity(ReversalPosition position, decimal entryPremium) => new()
    {
        Strategy = SharedModels.StrategyType.Reversal,
        Underlying = _target.Underlying,
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
        BrokenLevel = position.Signal.SweepLowSpot,
        StopLossSpot = position.StopLossSpot,
        Status = SharedModels.PositionStatus.Open,
        LastKnownPremium = entryPremium,
        LastUpdateTime = position.OpenedAt,
        CreatedAt = _clock.UtcNow
    };

    private async Task LogSignalAsync(ReversalSignal signal, bool positionOpened, int? positionId, string? skipReason, CancellationToken ct)
    {
        var entry = new SharedModels.SignalLogEntity
        {
            Strategy = SharedModels.StrategyType.Reversal,
            Underlying = _target.Underlying,
            Direction = "Bullish",
            SignalTime = signal.ConfirmedAtCandleTime,
            SpotPrice = signal.EntrySpot,
            BrokenLevel = signal.SweepLowSpot,
            IsCatchUp = false,
            PositionOpened = positionOpened,
            PositionId = positionId,
            SkipReason = skipReason,
            CreatedAt = _clock.UtcNow
        };
        await _signalLogRepository.LogAsync(entry, ct);
    }
}