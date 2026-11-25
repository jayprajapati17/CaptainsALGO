using AlgoData.Models;
using AlgoWorker.EmaCross.Core;
using AlgoWorker.Configuration;
using AlgoWorker.Hubs;
using AlgoWorker.Models;
using AlgoWorker.Services;
using AlgoWorker.Services.Persistence;
using Microsoft.AspNetCore.SignalR;
using SharedModels = AlgoData.Models;

namespace AlgoWorker.EmaCross;

/// <summary>
/// One EMA 9/21 strategy (A or B): the dependency-free <see cref="StrategyEngine"/> plus the paper-trading executor around it.
///
/// How the engine's "fill at the next open" maps to live: the engine is fed each CLOSED candle (Upstox's own candle data).
/// Right after, <see cref="StrategyEngine.PendingExit"/> / <see cref="StrategyEngine.PendingEntry"/> tell this class what to do
/// at the next open, which is now -- the new candle has just started. A signal on the day's last candle is carried to the next
/// session open (section 9.2). The index stop-loss is checked on every index tick; breakeven / trailing levels are taken from
/// the engine at candle close (section 9.3), so live matches the backtest.
/// </summary>
public sealed class EmaCrossRunner
{
    private readonly EmaCrossVariant _v;
    private readonly EmaCrossOptions _opt;
    private readonly UpstoxOptions _upstox;
    private readonly EmaCrossOptionSelector _selector;
    private readonly UpstoxRestClient _rest;
    private readonly UpstoxWebSocketClient _ws;
    private readonly TelegramAlertService _telegram;
    private readonly IPositionRepository _positions;
    private readonly ISignalLogRepository _signalLog;
    private readonly IHubContext<PositionHub> _hub;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    private readonly StrategyEngine _engine;
    private readonly CprTracker _cpr = new();
    private readonly WilderAdx _adx;
    private double? _prevAdx;
    private double? _lastSignalAdx;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private EmaCrossLeg? _leg;
    private bool _deferredToOpen;

    public EmaCrossRunner(EmaCrossVariant variant, EmaCrossOptions options, UpstoxOptions upstox, EmaCrossOptionSelector selector,
        UpstoxRestClient rest, UpstoxWebSocketClient ws, TelegramAlertService telegram, IPositionRepository positions,
        ISignalLogRepository signalLog, IHubContext<PositionHub> hub, IClock clock, ILogger logger)
    {
        _v = variant; _opt = options; _upstox = upstox; _selector = selector; _rest = rest; _ws = ws; _telegram = telegram;
        _positions = positions; _signalLog = signalLog; _hub = hub; _clock = clock; _logger = logger;
        _engine = new StrategyEngine(variant.Engine, variant.CreateFilter());
        _adx = new WilderAdx(variant.AdxPeriod);
    }

    public string Name => _v.Name;
    public int CandleMinutes => _v.CandleMinutes;
    public bool HasOpenLeg => _leg != null;

    // ------------------------------------------------------------------ candles

    /// <summary>Feeds one CLOSED candle of this strategy's timeframe (history replay when <paramref name="seeding"/>).</summary>
    public async Task OnCandleAsync(Bar bar, bool seeding, bool lastOfDay, CancellationToken ct)
    {
        var cpr = _cpr.Update(bar);
        var adxRaw = _adx.Update(bar);
        double? adx = double.IsNaN(adxRaw) ? null : adxRaw;

        _engine.OnCandleClosed(bar, new FilterContext(bar, adx, _prevAdx, cpr));
        _lastSignalAdx = adx;
        _prevAdx = adx;
        if (seeding) return;

        await _gate.WaitAsync(ct);
        try
        {
            // The engine closed its position on this candle (stop touched, found in candle data): make sure the leg follows.
            if (_leg != null && _engine.OpenPosition == null)
            {
                var last = _engine.Trades.LastOrDefault();
                await CloseLegAsync(last is null ? "Engine is flat" : $"{Describe(last.Reason)} (candle {bar.Time:HH:mm})", ct);
            }

            if (lastOfDay && (_engine.PendingExit || _engine.PendingEntry != null))
            {
                _deferredToOpen = true;   // section 9.2: parity = act at the next session's open
                await _telegram.SendStatusAsync($"{_v.Label}: signal on the day's last candle ({bar.Time:HH:mm}) -- will act at tomorrow's open " +
                                                $"(entry {_engine.PendingEntry?.ToString() ?? "none"}, exit {(_engine.PendingExit ? "yes" : "no")}).", ct);
                return;
            }

            await ExecutePendingAsync(ct);
            await SyncStopAsync(ct);
            await PersistLiveAsync(ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Live restart: forget simulated trades of the replayed history; keep the warmed EMA / ADX / CPR state.</summary>
    public void FinishSeeding() => _engine.ResetPositionState();

    /// <summary>First thing each trading day once the market is confirmed open: carry out a deferred signal, then check the expiry roll.</summary>
    public async Task OnSessionOpenAsync(DateOnly today, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_deferredToOpen)
            {
                _deferredToOpen = false;
                await ExecutePendingAsync(ct);
                await SyncStopAsync(ct);
            }

            if (_leg is { } leg && leg.Instrument.Expiry.DayNumber - today.DayNumber <= _opt.RollWhenDaysToExpiryAtMost)
            {
                var side = leg.Side;
                await CloseLegAsync($"Roll: expiry {leg.Instrument.Expiry:dd-MMM} is {leg.Instrument.Expiry.DayNumber - today.DayNumber} day(s) away", ct);
                await OpenLegAsync(side, signalNote: "roll", ct);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task ExecutePendingAsync(CancellationToken ct)
    {
        if (_engine.PendingExit && _leg != null)
            await CloseLegAsync("Opposite EMA 9/21 crossover (exit at next open)", ct);

        if (_engine.PendingEntry is Side side && _leg == null)
            await OpenLegAsync(side, signalNote: null, ct);
    }

    private async Task SyncStopAsync(CancellationToken ct)
    {
        if (_leg is not { } leg || _engine.OpenPosition is not { } p || (int)p.Side != (int)leg.Side) return;
        var stop = (decimal)p.Stop;
        if (stop == leg.IndexStop) return;

        leg.IndexStop = stop;
        if (leg.Id >= 0)
            await _positions.UpdateStopLossAsync(leg.Id, leg.PremiumStop, leg.IndexStop, ct);
        await BroadcastAsync(leg, "Open", null, ct);
        _logger.LogInformation("EMA 9/21 {Name}: index stop moved to {Stop:N2}.", _v.Name, stop);
    }

    private async Task PersistLiveAsync(CancellationToken ct)
    {
        if (_leg is { Id: >= 0 } leg)
        {
            await _positions.UpdateLivePremiumAsync(leg.Id, leg.LastPremium, DateTimeOffset.UtcNow, ct);
            await BroadcastAsync(leg, "Open", null, ct);
        }
    }

    // ------------------------------------------------------------------ ticks

    /// <summary>Index SL on every Nifty tick (section 9.3). Skipped (not queued) while an order is being processed.</summary>
    public async Task OnIndexTickAsync(decimal price, CancellationToken ct)
    {
        if (_leg is not { } leg) return;
        bool hit = leg.Side == Side.Long ? price <= leg.IndexStop : price >= leg.IndexStop;
        if (!hit) return;

        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            if (_leg is not { } l || l != leg) return;
            await CloseLegAsync($"Index stop-loss hit: Nifty {price:N2} vs stop {l.IndexStop:N2}" + (l.StopMoved ? " (breakeven / trailing)" : ""), ct);
        }
        finally { _gate.Release(); }
    }

    public async Task OnOptionTickAsync(MarketTick tick, CancellationToken ct)
    {
        if (_leg is not { } leg || leg.Instrument.InstrumentKey != tick.InstrumentKey) return;
        leg.LastPremium = tick.LastTradedPrice;

        if (leg.PremiumStop > 0 && tick.LastTradedPrice <= leg.PremiumStop)
        {
            if (!await _gate.WaitAsync(0, ct)) return;
            try
            {
                if (_leg is { } l && l == leg)
                    await CloseLegAsync($"Premium safety cap hit: ₹{tick.LastTradedPrice:N2} <= ₹{leg.PremiumStop:N2} ({_v.PremiumStopPercent():0}% below entry)", ct);
            }
            finally { _gate.Release(); }
            return;
        }

        if (DateTimeOffset.UtcNow - leg.LastBroadcastAt >= TimeSpan.FromSeconds(1))
        {
            leg.LastBroadcastAt = DateTimeOffset.UtcNow;
            await BroadcastAsync(leg, "Open", null, ct);
        }
    }

    // ------------------------------------------------------------------ leg lifecycle (paper)

    private async Task OpenLegAsync(Side side, string? signalNote, CancellationToken ct)
    {
        var today = _clock.Today;
        var dir = side == Side.Long ? "Up" : "Down";
        decimal spot;
        try { spot = await _rest.GetLastTradedPriceAsync(_upstox.NiftyInstrumentKey, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnauthorizedTokenException)
        {
            _logger.LogError(ex, "EMA 9/21 {Name}: could not read the Nifty spot -- {Side} entry skipped.", _v.Name, side);
            await LogSignalAsync(dir, 0, opened: false, null, "Nifty spot unavailable", ct);
            return;
        }

        var (choice, skip) = await _selector.SelectAsync(side, today, _v.ExpiryRule(), ct);
        if (choice is null)
        {
            _logger.LogWarning("EMA 9/21 {Name}: {Side} signal skipped -- {Reason}", _v.Name, side, skip);
            await _telegram.SendWarningAsync($"{_v.Label}: {side} signal NOT traded -- {skip}.", ct);
            await LogSignalAsync(dir, spot, opened: false, null, skip, ct);
            return;
        }

        int d = (int)side;
        var stop = spot - d * (decimal)_v.Engine.StopPoints;
        var pct = (decimal)_v.PremiumStopPercent();
        var leg = new EmaCrossLeg
        {
            Side = side,
            Instrument = choice.Instrument,
            LotSize = _opt.LotSize,
            IndexEntry = spot,
            IndexStop = stop,
            InitialIndexStop = stop,
            EntryPremium = choice.Premium,
            LastPremium = choice.Premium,
            PremiumStop = Math.Round(choice.Premium * (1 - pct / 100m), 2),
            OpenedAt = DateTimeOffset.UtcNow
        };

        leg.Id = await _positions.SaveOpenedAsync(ToEntity(leg, dir), ct);
        _leg = leg;
        await LogSignalAsync(dir, spot, opened: leg.Id >= 0, leg.Id >= 0 ? leg.Id : null, leg.Id < 0 ? "Position DB write failed (see logs)" : signalNote, ct);

        // The subscription is kept while the trade is open; the shared feed re-subscribes it after every daily reconnect.
        await _ws.SubscribeAsync(choice.Instrument.InstrumentKey, ct);
        await _telegram.SendStatusAsync(
            $"{_v.Label}: PAPER BUY {choice.Instrument.TradingSymbol} @ ₹{choice.Premium:N2} (lot {leg.LotSize}). " +
            $"Nifty {spot:N2}, index stop {stop:N2} ({_v.Engine.StopPoints:0} pts), premium cap ₹{leg.PremiumStop:N2}, expiry {choice.Instrument.Expiry:dd-MMM-yyyy}." +
            (signalNote is null ? "" : $" ({signalNote})"), ct);
        await BroadcastAsync(leg, "Open", null, ct);
        _logger.LogInformation("EMA 9/21 {Name}: opened {Symbol} @ {Premium} (spot {Spot}, stop {Stop}).", _v.Name, choice.Instrument.TradingSymbol, choice.Premium, spot, stop);
    }

    private async Task CloseLegAsync(string reason, CancellationToken ct)
    {
        if (_leg is not { } leg) return;
        _leg = null;   // first, so a concurrent tick can't close it twice

        try { leg.LastPremium = await _rest.GetLastTradedPriceAsync(leg.Instrument.InstrumentKey, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "EMA 9/21 {Name}: could not fetch the exit LTP -- using the last tick ({Premium}).", _v.Name, leg.LastPremium);
        }

        await _ws.UnsubscribeAsync(leg.Instrument.InstrumentKey, ct);
        if (leg.Id >= 0)
            await _positions.CloseAsync(leg.Id, leg.LastPremium, DateTimeOffset.UtcNow, reason, leg.PnlRupees, leg.PnlPercent, ct);
        await BroadcastAsync(leg, "Closed", reason, ct);
        await _telegram.SendStatusAsync(
            $"{_v.Label}: PAPER SELL {leg.Instrument.TradingSymbol} @ ₹{leg.LastPremium:N2}. {reason}. " +
            $"P&L ₹{leg.PnlRupees:N0} ({leg.PnlPercent:+0.0;-0.0}%).", ct);
        _logger.LogInformation("EMA 9/21 {Name}: closed {Symbol} -- {Reason} -- P&L {Pnl:N2}.", _v.Name, leg.Instrument.TradingSymbol, reason, leg.PnlRupees);
    }

    // ------------------------------------------------------------------ Dashboard / API

    public async Task<bool> ForceCloseAsync(int id, CancellationToken ct)
    {
        if (_leg is not { } leg || leg.Id != id) return false;
        await _gate.WaitAsync(ct);
        try
        {
            if (_leg is not { } l || l.Id != id) return false;
            await CloseLegAsync("Manually closed from Dashboard", ct);
            return true;
        }
        finally { _gate.Release(); }
    }

    public IEnumerable<PositionUpdateDto> GetOpenPositionsSnapshot() =>
        _leg is { } leg ? new[] { ToDto(leg, "Open", null) } : Enumerable.Empty<PositionUpdateDto>();

    private PositionUpdateDto ToDto(EmaCrossLeg p, string status, string? exitReason) => new(
        PositionId: p.Id,
        Strategy: _v.Strategy.ToString(),
        Direction: p.Side == Side.Long ? "Up" : "Down",
        Leg: null,
        TradingSymbol: p.Instrument.TradingSymbol,
        Strike: p.Instrument.Strike,
        OptionType: p.IsCall ? "CE" : "PE",
        Expiry: p.Instrument.Expiry,
        EntryPremium: p.EntryPremium,
        EntryTime: p.OpenedAt,
        CurrentPremium: p.LastPremium,
        PnlRupees: p.PnlRupees,
        PnlPercent: p.PnlPercent,
        Status: status,
        ExitReason: exitReason,
        StopLossPremium: p.PremiumStop,
        StopLossSpot: p.IndexStop,
        StopLossTrailing: p.StopMoved,
        Underlying: Underlyings.Nifty50);

    private Task BroadcastAsync(EmaCrossLeg leg, string status, string? exitReason, CancellationToken ct) =>
        PositionBroadcast.PublishAsync(_hub, ToDto(leg, status, exitReason), _logger, ct);

    private SharedModels.PositionEntity ToEntity(EmaCrossLeg leg, string direction) => new()
    {
        Strategy = _v.Strategy,
        Direction = direction,
        Leg = null,
        Underlying = Underlyings.Nifty50,
        InstrumentKey = leg.Instrument.InstrumentKey,
        TradingSymbol = leg.Instrument.TradingSymbol,
        Strike = leg.Instrument.Strike,
        OptionType = leg.IsCall ? SharedModels.OptionType.CE : SharedModels.OptionType.PE,
        Expiry = leg.Instrument.Expiry,
        LotSize = leg.LotSize,
        SpotAtEntry = leg.IndexEntry,
        EntryPremium = leg.EntryPremium,
        EntryTime = leg.OpenedAt,
        AdxAtEntry = _lastSignalAdx,
        Status = SharedModels.PositionStatus.Open,
        LastKnownPremium = leg.EntryPremium,
        LastUpdateTime = leg.OpenedAt,
        StopLossPremium = leg.PremiumStop,
        StopLossSpot = leg.IndexStop,
        CreatedAt = _clock.UtcNow
    };

    private Task LogSignalAsync(string direction, decimal spot, bool opened, int? positionId, string? note, CancellationToken ct) =>
        _signalLog.LogAsync(new SharedModels.SignalLogEntity
        {
            Strategy = _v.Strategy,
            Underlying = Underlyings.Nifty50,
            Direction = direction,
            SignalTime = DateTimeOffset.UtcNow,
            SpotPrice = spot,
            AdxAtSignal = _lastSignalAdx,
            IsCatchUp = false,
            PositionOpened = opened,
            PositionId = positionId,
            SkipReason = opened ? null : note,
            CreatedAt = _clock.UtcNow
        }, ct);

    private static string Describe(ExitReason r) => r switch
    {
        ExitReason.StopLoss => "Index stop-loss",
        ExitReason.BreakevenOrTrail => "Index stop-loss (breakeven / trailing)",
        ExitReason.Crossover => "Opposite crossover",
        _ => r.ToString()
    };
}
