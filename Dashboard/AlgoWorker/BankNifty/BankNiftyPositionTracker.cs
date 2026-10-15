using AlgoData.Models;
using AlgoWorker.Configuration;
using AlgoWorker.Hubs;
using AlgoWorker.Models;
using AlgoWorker.Services.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using OptionType = AlgoWorker.Models.OptionType;

namespace AlgoWorker.BankNifty;

using AlgoWorker.Services;

/// <summary>
/// Lifecycle of the single open virtual Bank Nifty position + all the risk gates:
/// Same rules as the Nifty ORB tracker: current-expiry ATM option, max trades/day.
/// Exits: premium trailing stop (every tick), force-exit time, manual close, market close.
/// </summary>
public sealed class BankNiftyPositionTracker
{
    private readonly OptionInstrumentResolver _resolver;
    private readonly UpstoxOptions _upstox;
    private readonly UpstoxRestClient _rest;
    private readonly UpstoxWebSocketClient _ws;
    private readonly BankNiftyAlertService _telegram;
    private readonly BankNiftyOptions _options;
    private readonly IPositionRepository _positions;
    private readonly ISignalLogRepository _signalLog;
    private readonly IHubContext<PositionHub> _hub;
    private readonly IClock _clock;
    private readonly ILogger<BankNiftyPositionTracker> _logger;

    private BankNiftyPosition? _active;
    private DateTimeOffset _lastStatusLogAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);   // signal handling vs tick/candle handling

    // Daily trade count -- loaded from the DB on the first signal of each day so a restart never resets it.
    private DateOnly _statsDay;
    private int _tradesToday;

    public BankNiftyPositionTracker(
        OptionInstrumentResolver resolver, UpstoxRestClient rest, UpstoxWebSocketClient ws, BankNiftyAlertService telegram,
        IOptions<UpstoxOptions> upstox, IOptions<BankNiftyOptions> options, IPositionRepository positions, ISignalLogRepository signalLog,
        IHubContext<PositionHub> hub, IClock clock, ILogger<BankNiftyPositionTracker> logger)
    {
        _resolver = resolver; _upstox = upstox.Value; _rest = rest; _ws = ws; _telegram = telegram;
        _options = options.Value; _positions = positions; _signalLog = signalLog;
        _hub = hub; _clock = clock; _logger = logger;
    }

    public async Task OnBreakoutConfirmedAsync(BankNiftySignal signal, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await HandleSignalAsync(signal, ct); }
        finally { _gate.Release(); }
    }

    private async Task HandleSignalAsync(BankNiftySignal signal, CancellationToken ct)
    {
        async Task Skip(string reason)
        {
            _logger.LogInformation("Bank Nifty {Dir} signal skipped: {Reason}", signal.Direction, reason);
            await LogSignalAsync(signal, false, null, reason, ct);
        }

        if (_active is { IsOpen: true }) { await Skip("A Bank Nifty position is already open"); return; }

        await EnsureStatsAsync(_clock.Today, ct);
        if (_tradesToday >= _options.BreakoutMaxTradesPerDay) { await Skip($"Daily limit of {_options.BreakoutMaxTradesPerDay} ORB trades reached"); return; }

        var type = signal.Direction == BankNiftyDirection.Up ? OptionType.Call : OptionType.Put;
        OptionInstrument? instrument;
        try
        {
            instrument = await _resolver.ResolveCurrentAtmAsync(
                _upstox.BankNiftyInstrumentKey, signal.SpotAtConfirmation, type, _options.StrikeStepPoints, _clock.Today, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Strike selection failed.");
            await _telegram.SendWarningAsync("Breakout confirmed but strike selection failed. No position opened.", ct);
            await Skip("Strike selection failed (see logs)");
            return;
        }
        if (instrument is null)
        {
            await _telegram.SendWarningAsync($"Breakout {signal.Direction} confirmed but could not resolve an ATM option instrument. No position opened.", ct);
            await Skip("Could not resolve an ATM option instrument");
            return;
        }

        decimal entry;
        try { entry = await _rest.GetLastTradedPriceAsync(instrument.InstrumentKey, ct); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch entry LTP for {Instrument} -- position not opened.", instrument.InstrumentKey);
            await _telegram.SendWarningAsync($"Breakout confirmed but could not fetch entry price for {instrument.TradingSymbol}. Position not opened.", ct);
            await Skip("Entry LTP fetch failed");
            return;
        }

        var now = _clock.UtcNow;
        var sl = BankNiftyStopCalculator.ComputeStopLoss(entry, entry, _options);
        var pos = new BankNiftyPosition
        {
            Signal = signal,
            Instrument = instrument,
            LotSize = _options.LotSize,
            EntryPremium = entry,
            OpenedAt = now,
            LastKnownPremium = entry,
            PeakPremium = entry,
            CurrentStopLossPremium = sl,
            LastUpdateSentAt = now
        };
        _active = pos;

        pos.Id = await _positions.SaveOpenedAsync(ToEntity(pos), ct);
        _tradesToday++;
        await LogSignalAsync(signal, pos.Id >= 0, pos.Id >= 0 ? pos.Id : null, pos.Id < 0 ? "Position DB write failed (see logs)" : null, ct);

        await _ws.SubscribeAsync(instrument.InstrumentKey, ct);
        await _telegram.SendEntryAlertAsync(pos, instrument.Expiry.DayNumber - _clock.Today.DayNumber, ct);
        await BroadcastAsync(pos, "Open", null, ct);
        _logger.LogInformation("Bank Nifty ORB {Dir} opened: {Symbol} @ {Premium}, SL {Sl}", signal.Direction, instrument.TradingSymbol, entry, sl);
    }

    /// <summary>Every option tick: peak, trailing SL (persisted when it moves), SL exit, throttled live broadcast.</summary>
    public async Task OnOptionTick(MarketTick tick, CancellationToken ct)
    {
        if (_active is not { IsOpen: true } pos || pos.Instrument.InstrumentKey != tick.InstrumentKey) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_active is not { IsOpen: true } p || p.Instrument.InstrumentKey != tick.InstrumentKey) return;

            p.LastKnownPremium = tick.LastTradedPrice;
            if (tick.LastTradedPrice > p.PeakPremium) p.PeakPremium = tick.LastTradedPrice;

            var newSl = BankNiftyStopCalculator.ComputeStopLoss(p.EntryPremium, p.PeakPremium, _options);
            if (newSl != p.CurrentStopLossPremium)
            {
                _logger.LogInformation("Stop-loss moved {Old:N2} -> {New:N2} (peak {Peak:N2}, LTP {Ltp:N2}) on {Symbol}",
                    p.CurrentStopLossPremium, newSl, p.PeakPremium, tick.LastTradedPrice, p.Instrument.TradingSymbol);
                p.CurrentStopLossPremium = newSl;
                await _positions.UpdateStopLossAsync(p.Id, newSl, null, ct);
            }
            p.TrailingActive = BankNiftyStopCalculator.IsTrailing(p.EntryPremium, p.PeakPremium, _options);

            if (tick.LastTradedPrice <= p.CurrentStopLossPremium)
            {
                await CloseAsync(p, $"Stop-loss hit at ₹{p.CurrentStopLossPremium:N2} (peak ₹{p.PeakPremium:N2})", ct);
                return;
            }

            if (_clock.UtcNow - _lastStatusLogAt >= TimeSpan.FromSeconds(30))
            {
                _lastStatusLogAt = _clock.UtcNow;
                _logger.LogInformation("Position {Symbol}: LTP {Ltp:N2} | entry {Entry:N2} | peak {Peak:N2} | SL {Sl:N2} | P&L {Pnl:N0} ({Pct:N1}%)",
                    p.Instrument.TradingSymbol, p.LastKnownPremium, p.EntryPremium, p.PeakPremium, p.CurrentStopLossPremium, p.PnlRupees, p.PnlPercent);
            }

            if (_clock.UtcNow - p.LastBroadcastAt >= TimeSpan.FromSeconds(1))
            {
                p.LastBroadcastAt = _clock.UtcNow;
                await BroadcastAsync(p, "Open", null, ct);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Once per closed 5-min candle: force-exit time, periodic update.</summary>
    public async Task OnCandleClosedAsync(Candle candle, CancellationToken ct)
    {
        if (_active is not { IsOpen: true }) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_active is not { IsOpen: true } p) return;

            var closedAt = TimeOfDay.Of(candle.OpenTime.AddMinutes(5));
            if (closedAt >= TimeOfDay.Parse(_options.BreakoutForceExitTime))
            {
                await CloseAsync(p, $"Force exit time reached ({_options.BreakoutForceExitTime})", ct);
                return;
            }

            if (_clock.UtcNow - p.LastUpdateSentAt >= TimeSpan.FromMinutes(_options.PositionUpdateIntervalMinutes))
            {
                p.LastUpdateSentAt = _clock.UtcNow;
                await _telegram.SendPositionUpdateAsync(p, ct);
                await _positions.UpdateLivePremiumAsync(p.Id, p.LastKnownPremium, _clock.UtcNow, ct);
                await BroadcastAsync(p, "Open", null, ct);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task CloseAsync(BankNiftyPosition p, string reason, CancellationToken ct)
    {
        p.IsOpen = false;
        var exitTime = _clock.UtcNow;
        var pnl = p.PnlRupees;

        await _telegram.SendExitAlertAsync(p, reason, ct);
        await _ws.UnsubscribeAsync(p.Instrument.InstrumentKey, ct);
        await _positions.CloseAsync(p.Id, p.LastKnownPremium, exitTime, reason, pnl, (double)p.PnlPercent, ct);
        await BroadcastAsync(p, "Closed", reason, ct);
        _logger.LogInformation("Bank Nifty position closed. {Reason}. P&L {Pnl:N2}", reason, pnl);
    }

    public async Task CloseForMarketCloseAsync(CancellationToken ct)
    {
        if (_active is not { IsOpen: true }) return;
        await _gate.WaitAsync(ct);
        try { if (_active is { IsOpen: true } p) await CloseAsync(p, "Market closed for the day", ct); }
        finally { _gate.Release(); }
    }

    public async Task<bool> ForceCloseAsync(int id, CancellationToken ct)
    {
        if (_active is not { IsOpen: true } || _active.Id != id) return false;
        await _gate.WaitAsync(ct);
        try
        {
            if (_active is not { IsOpen: true } p || p.Id != id) return false;
            await CloseAsync(p, "Manually closed from Dashboard", ct);
            return true;
        }
        finally { _gate.Release(); }
    }

    public IEnumerable<PositionUpdateDto> GetOpenPositionsSnapshot() =>
        _active is { IsOpen: true } p ? new[] { ToDto(p, "Open", null) } : Enumerable.Empty<PositionUpdateDto>();

    private async Task EnsureStatsAsync(DateOnly today, CancellationToken ct)
    {
        if (_statsDay == today) return;
        _statsDay = today;
        (_tradesToday, _) = await _positions.GetDayStatsAsync(today, ct);
        _logger.LogInformation("Bank Nifty ORB: {Trades} trade(s) already opened today.", _tradesToday);
    }

    private static PositionUpdateDto ToDto(BankNiftyPosition p, string status, string? exitReason) => new(
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
        PnlPercent: (double)p.PnlPercent,
        Status: status,
        ExitReason: exitReason,
        StopLossPremium: p.CurrentStopLossPremium,
        Underlying: Underlyings.BankNifty);

    private async Task BroadcastAsync(BankNiftyPosition p, string status, string? exitReason, CancellationToken ct)
    {
        await PositionBroadcast.PublishAsync(_hub, ToDto(p, status, exitReason), _logger, ct);
    }

    private PositionEntity ToEntity(BankNiftyPosition p) => new()
    {
        Strategy = StrategyType.Breakout,
        Direction = p.Signal.Direction.ToString(),
        Underlying = Underlyings.BankNifty,
        InstrumentKey = p.Instrument.InstrumentKey,
        TradingSymbol = p.Instrument.TradingSymbol,
        Strike = p.Instrument.Strike,
        OptionType = p.Instrument.Type == OptionType.Call ? AlgoData.Models.OptionType.CE : AlgoData.Models.OptionType.PE,
        Expiry = p.Instrument.Expiry,
        LotSize = p.LotSize,
        SpotAtEntry = p.Signal.SpotAtConfirmation,
        EntryPremium = p.EntryPremium,
        EntryTime = p.OpenedAt,
        BrokenLevel = p.Signal.BrokenLevel,
        StopLossPremium = p.CurrentStopLossPremium,
        Status = PositionStatus.Open,
        LastKnownPremium = p.EntryPremium,
        LastUpdateTime = p.OpenedAt,
        CreatedAt = _clock.UtcNow
    };

    private async Task LogSignalAsync(BankNiftySignal s, bool opened, int? positionId, string? skipReason, CancellationToken ct) =>
        await _signalLog.LogAsync(new SignalLogEntity
        {
            Strategy = StrategyType.Breakout,
            Underlying = Underlyings.BankNifty,
            Direction = s.Direction.ToString(),
            SignalTime = s.ConfirmedAt,
            SpotPrice = s.SpotAtConfirmation,
            BrokenLevel = s.BrokenLevel,
            IsCatchUp = false,
            PositionOpened = opened,
            PositionId = positionId,
            SkipReason = skipReason,
            CreatedAt = _clock.UtcNow
        }, ct);
}