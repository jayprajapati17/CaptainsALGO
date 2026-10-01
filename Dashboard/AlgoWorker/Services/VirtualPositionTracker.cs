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
/// Owns the lifecycle of the active virtual position(s). Only ONE signal's
/// trade can be "in play" at a time (user requirement: no simultaneous long +
/// short), but per the user's latest request, each accepted signal now opens
/// TWO legs together -- a current-week ITM contract and a next-week ATM
/// contract -- both tracked and alerted independently, and both must close
/// before a new signal is allowed to open the next pair.
/// </summary>
public sealed class VirtualPositionTracker
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
    private readonly ILogger<VirtualPositionTracker> _logger;

    // >>> CHANGED: was a single `_activePosition`. Now a list because every
    // signal opens up to 2 legs (current-week ITM + next-week ATM) together.
    private readonly List<VirtualPosition> _activePositions = new();

    public VirtualPositionTracker(
        OptionInstrumentResolver instrumentResolver,
        UpstoxRestClient restClient,
        UpstoxWebSocketClient webSocketClient,
        TelegramAlertService telegram,
        IOptions<StrategyOptions> options,
        IPositionRepository positionRepository,
        ISignalLogRepository signalLogRepository,
        IHubContext<PositionHub> hub,
        IClock clock,
        ILogger<VirtualPositionTracker> logger)
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

    private bool AnyLegOpen => _activePositions.Any(p => p.IsOpen);

    public async Task OnSignalConfirmedAsync(TradeSignal signal, CancellationToken ct)
    {
        // Only one signal's pair of legs can be open at a time.
        if (AnyLegOpen)
        {
            var openDirs = string.Join(", ", _activePositions.Where(p => p.IsOpen).Select(p => p.Leg));
            _logger.LogInformation(
                "Legs still open ({OpenLegs}) -- ignoring new {NewDirection} signal (only one signal's trade at a time).",
                openDirs, signal.Direction);
            await LogSignalAsync(signal, positionOpened: false, positionId: null,
                skipReason: $"Legs still open from a previous signal ({openDirs})", ct);
            return;
        }

        // Clear out any fully-closed legs from the previous signal before starting fresh.
        _activePositions.Clear();

        var resolvedLegs = await _instrumentResolver.ResolveAllLegsAsync(signal, ct);
        if (resolvedLegs.Count == 0)
        {
            await _telegram.SendWarningAsync(
                $"{signal.Direction} confirmed but neither leg (current-week ITM / next-week ATM) could be resolved (check logs). No virtual positions opened.", ct);
            await LogSignalAsync(signal, positionOpened: false, positionId: null,
                skipReason: "Neither leg (current-week ITM / next-week ATM) could be resolved", ct);
            return;
        }

        int? firstOpenedDbId = null;
        foreach (var resolvedLeg in resolvedLegs)
        {
            var dbId = await OpenLegAsync(signal, resolvedLeg, ct);
            firstOpenedDbId ??= dbId;
        }

        await LogSignalAsync(signal, positionOpened: firstOpenedDbId is not null, positionId: firstOpenedDbId,
            skipReason: firstOpenedDbId is null ? "Both legs failed to open (see logs -- likely an LTP fetch failure)" : null, ct);
    }

    /// <summary>Opens one leg. Returns its DB-assigned Id, or null if the leg failed to open (LTP fetch failure).</summary>
    private async Task<int?> OpenLegAsync(TradeSignal signal, ResolvedLeg resolvedLeg, CancellationToken ct)
    {
        var instrument = resolvedLeg.Instrument;

        decimal entryPremium;
        try
        {
            entryPremium = await _restClient.GetLastTradedPriceAsync(instrument.InstrumentKey, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch entry LTP for {Instrument} ({Leg}) -- this leg not opened.",
                instrument.InstrumentKey, resolvedLeg.Leg);
            await _telegram.SendWarningAsync(
                $"Signal confirmed but could not fetch entry price for {instrument.TradingSymbol} ({resolvedLeg.Leg}). This leg not opened.", ct);
            return null;
        }

        var position = new VirtualPosition
        {
            Signal = signal,
            Instrument = instrument,
            Leg = resolvedLeg.Leg,
            LotSize = _options.LotSize,
            EntryPremium = entryPremium,
            OpenedAt = DateTimeOffset.Now,
            LastKnownPremium = entryPremium,
            AdxPeakSinceEntry = signal.AdxAtConfirmation,
            LastUpdateSentAt = DateTimeOffset.Now,
            PeakPremium = entryPremium,
            CurrentStopLossPremium = TrailingStopCalculator.ComputeStopLoss(entryPremium, entryPremium, _options)
        };

        _activePositions.Add(position);

        // >>> NEW (Task 3): persist, then use the DB-assigned Id as this
        // position's identity going forward (Command API, SignalR, snapshot).
        // -1 means the write failed -- non-fatal (matches PositionRepository's
        // own "never let a DB hiccup interrupt live trading" philosophy);
        // the leg still trades and alerts normally, it just won't have a
        // durable Id until the next successful write (there isn't one for
        // this leg's lifetime today, so it simply won't appear in
        // Dashboard's Live/History views -- Telegram alerts are unaffected).
        var dbId = await _positionRepository.SaveOpenedAsync(ToEntity(position, entryPremium), ct);
        position.Id = dbId;

        await _webSocketClient.SubscribeAsync(instrument.InstrumentKey, ct);
        await _telegram.SendEntryAlertAsync(signal, resolvedLeg, entryPremium, _options.LotSize, position.CurrentStopLossPremium, ct);
        await BroadcastAsync(position, "Open", null, ct);

        _logger.LogInformation("Virtual {Direction} position opened [{Leg}]: {Symbol} @ {Premium}",
            signal.Direction, resolvedLeg.Leg, instrument.TradingSymbol, entryPremium);

        return dbId >= 0 ? dbId : null;
    }

    /// <summary>
    /// Checks each open leg's stop-loss / trailing stop-loss on EVERY tick (in
    /// addition to the existing per-candle exits: opposite crossover, expiry
    /// day, hard EMA50 stop, ADX flat/decline) using the shared 3-phase
    /// "Final Updated Trailing Rule" (see TrailingStopCalculator.cs).
    /// </summary>
    public async Task OnOptionTick(MarketTick tick, CancellationToken ct)
    {
        foreach (var position in _activePositions.ToList())
        {
            if (!position.IsOpen || position.Instrument.InstrumentKey != tick.InstrumentKey)
                continue;

            position.LastKnownPremium = tick.LastTradedPrice;

            if (tick.LastTradedPrice > position.PeakPremium)
                position.PeakPremium = tick.LastTradedPrice;

            position.CurrentStopLossPremium = TrailingStopCalculator.ComputeStopLoss(position.EntryPremium, position.PeakPremium, _options);
            position.TrailingActive = position.PeakPremium - position.EntryPremium >= (decimal)_options.TrailingPhase2TriggerPoints;

            if (tick.LastTradedPrice <= position.CurrentStopLossPremium)
            {
                var reason = $"Stop-loss hit at ₹{position.CurrentStopLossPremium:N2} (peak ₹{position.PeakPremium:N2})";
                await ClosePositionAsync(position, reason, ct);
                continue;
            }

            // >>> NEW (Task 8): push the live premium/P&L to the Dashboard, throttled
            // to ~1/sec per leg so a busy tick stream doesn't flood the hub.
            if (position.IsOpen && DateTimeOffset.Now - position.LastBroadcastAt >= TimeSpan.FromSeconds(1))
            {
                position.LastBroadcastAt = DateTimeOffset.Now;
                await BroadcastAsync(position, "Open", null, ct);
            }
        }
    }

    /// <summary>Call once per closed Nifty candle -- drives both the periodic P&amp;L update and the exit checks for BOTH legs.</summary>
    public async Task OnCandleClosedAsync(IndicatorEngine engine, IndicatorSnapshot current, bool isExpiryDay, CancellationToken ct)
    {
        // Snapshot the list since ClosePositionAsync doesn't mutate _activePositions itself.
        foreach (var position in _activePositions.ToList())
        {
            if (!position.IsOpen) continue;
            await EvaluatePositionAsync(position, engine, current, isExpiryDay, ct);
        }
    }

    private async Task EvaluatePositionAsync(
        VirtualPosition position, IndicatorEngine engine, IndicatorSnapshot current, bool isExpiryDay, CancellationToken ct)
    {
        if (current.Adx > position.AdxPeakSinceEntry)
            position.AdxPeakSinceEntry = current.Adx;

        var (shouldExit, reason) = EvaluateExit(position, engine, current, isExpiryDay);
        if (shouldExit)
        {
            await ClosePositionAsync(position, reason!, ct);
            return;
        }

        if (DateTimeOffset.Now - position.LastUpdateSentAt >= TimeSpan.FromMinutes(_options.PositionUpdateIntervalMinutes))
        {
            await _telegram.SendPositionUpdateAsync(position, ct);
            await _positionRepository.UpdateLivePremiumAsync(position.Id, position.LastKnownPremium, DateTimeOffset.Now, ct);
            await BroadcastAsync(position, "Open", null, ct);
            position.LastUpdateSentAt = DateTimeOffset.Now;
        }
    }

    private (bool ShouldExit, string? Reason) EvaluateExit(
        VirtualPosition position, IndicatorEngine engine, IndicatorSnapshot current, bool isExpiryDay)
    {
        // --- Safety-net exits (always checked first, regardless of ADX behaviour) ---
        var isLong = position.Signal.Direction == SignalDirection.GoldenCross;
        var oppositeCrossoverHappened = isLong ? !current.Ema21AboveEma50 : current.Ema21AboveEma50;
        if (oppositeCrossoverHappened)
            return (true, "Opposite EMA21/50 crossover occurred");

        // Current-week ITM leg has its OWN, earlier expiry than the next-week
        // ATM leg -- so "is today an expiry day" needs to be leg-aware. The
        // next-week leg should NOT be force-closed on the current week's Tuesday.
        if (isExpiryDay && position.Leg == OptionLeg.CurrentWeekItm)
            return (true, "Current-week option expiry day reached (forced exit)");

        var hardStopBreached = isLong
            ? current.Close < (decimal)current.Ema50
            : current.Close > (decimal)current.Ema50;
        if (hardStopBreached)
            return (true, $"Hard stop-loss: price closed against EMA50 ({current.Ema50:N2})");

        // --- ADX-based exit (design doc 3.5.1) ---
        var lookback = engine.NCandlesAgo(_options.AdxFlatLookbackBars);
        if (lookback is not null && Math.Abs(current.Adx - lookback.Adx) < _options.AdxFlatThresholdPoints)
            return (true, $"ADX flattened ({lookback.Adx:N1} -> {current.Adx:N1} over {_options.AdxFlatLookbackBars} candles)");

        var peakDrop = position.AdxPeakSinceEntry - current.Adx;
        if (peakDrop >= _options.AdxDeclineThresholdPoints)
            return (true, $"ADX declined {peakDrop:N1} points from its post-entry peak ({position.AdxPeakSinceEntry:N1} -> {current.Adx:N1})");

        var consecutiveFalling = true;
        for (var i = 0; i < _options.AdxDeclineConsecutiveBars; i++)
        {
            var a = engine.NCandlesAgo(i);
            var b = engine.NCandlesAgo(i + 1);
            if (a is null || b is null || a.Adx >= b.Adx) { consecutiveFalling = false; break; }
        }
        if (consecutiveFalling)
            return (true, $"ADX falling for {_options.AdxDeclineConsecutiveBars} consecutive candles");

        return (false, null);
    }

    private async Task ClosePositionAsync(VirtualPosition position, string reason, CancellationToken ct)
    {
        position.IsOpen = false;
        var exitTime = DateTimeOffset.Now;
        await _telegram.SendExitAlertAsync(position, reason, ct);
        await _webSocketClient.UnsubscribeAsync(position.Instrument.InstrumentKey, ct);
        await _positionRepository.CloseAsync(position.Id, position.LastKnownPremium, exitTime, reason, position.PnlRupees, position.PnlPercent, ct);
        await BroadcastAsync(position, "Closed", reason, ct);

        _logger.LogInformation("Virtual {Direction} position closed [{Leg}]. Reason: {Reason}. P&L: {Pnl:N2} ({PnlPct:N1}%)",
            position.Signal.Direction, position.Leg, reason, position.PnlRupees, position.PnlPercent);
    }

    /// <summary>Force-close any open legs, e.g. at market close for the day.</summary>
    public async Task CloseAllForMarketCloseAsync(CancellationToken ct)
    {
        foreach (var position in _activePositions.Where(p => p.IsOpen).ToList())
            await ClosePositionAsync(position, "Market closed for the day", ct);
    }

    // >>> NEW (Task 5): Command API support.

    /// <summary>Manual close from the Dashboard's "Close Position" button (POST /api/positions/{id}/close). Returns false if no OPEN leg with this Id exists here.</summary>
    public async Task<bool> ForceCloseAsync(int id, CancellationToken ct)
    {
        var position = _activePositions.FirstOrDefault(p => p.Id == id && p.IsOpen);
        if (position is null)
            return false;

        await ClosePositionAsync(position, "Manually closed from Dashboard", ct);
        return true;
    }

    /// <summary>Snapshot of currently OPEN legs for GET /api/positions/open (also what the Dashboard's Live page loads on first paint, before SignalR takes over).</summary>
    public IEnumerable<PositionUpdateDto> GetOpenPositionsSnapshot()
    {
        return _activePositions
            .Where(p => p.IsOpen)
            .Select(p => ToDto(p, "Open", null));
    }

    // >>> NEW (Task 3 + Task 4): DB persistence + SignalR broadcast helpers.

    private static PositionUpdateDto ToDto(VirtualPosition p, string status, string? exitReason) => new(
        PositionId: p.Id,
        Strategy: "Ema",
        Direction: p.Signal.Direction.ToString(),
        Leg: p.Leg.ToString(),
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
        ExitReason: exitReason);

    /// <summary>Broadcasts on PositionHub's "PositionChanged" event -- the Dashboard's Live page (Task 8) listens for this.</summary>
    private async Task BroadcastAsync(VirtualPosition position, string status, string? exitReason, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync("PositionChanged", ToDto(position, status, exitReason), ct);
        }
        catch (Exception ex)
        {
            // Non-fatal -- same reasoning as the repositories: a Dashboard/SignalR
            // hiccup should never interrupt live trading logic or Telegram alerts.
            _logger.LogError(ex, "Failed to broadcast PositionChanged for PositionId {Id}.", position.Id);
        }
    }

    private SharedModels.PositionEntity ToEntity(VirtualPosition position, decimal entryPremium) => new()
    {
        Strategy = SharedModels.StrategyType.Ema,
        Direction = position.Signal.Direction.ToString(),
        Leg = position.Leg == OptionLeg.CurrentWeekItm ? SharedModels.PositionLeg.CurrentWeekItm : SharedModels.PositionLeg.NextWeekAtm,
        InstrumentKey = position.Instrument.InstrumentKey,
        TradingSymbol = position.Instrument.TradingSymbol,
        Strike = position.Instrument.Strike,
        OptionType = position.Instrument.Type == OptionType.Call ? SharedModels.OptionType.CE : SharedModels.OptionType.PE,
        Expiry = position.Instrument.Expiry,
        LotSize = position.LotSize,
        SpotAtEntry = position.Signal.NiftySpotAtConfirmation,
        EntryPremium = entryPremium,
        EntryTime = position.OpenedAt,
        Confidence = MapConfidence(position.Signal.Confidence),
        AdxAtEntry = position.Signal.AdxAtConfirmation,
        AdxNCandlesAgo = position.Signal.AdxNCandlesAgo,
        Status = SharedModels.PositionStatus.Open,
        LastKnownPremium = entryPremium,
        LastUpdateTime = position.OpenedAt,
        CreatedAt = _clock.Now
    };

    private async Task LogSignalAsync(TradeSignal signal, bool positionOpened, int? positionId, string? skipReason, CancellationToken ct)
    {
        var entry = new SharedModels.SignalLogEntity
        {
            Strategy = SharedModels.StrategyType.Ema,
            Direction = signal.Direction.ToString(),
            Confidence = MapConfidence(signal.Confidence),
            SignalTime = signal.ConfirmedAtCandleTime,
            SpotPrice = signal.NiftySpotAtConfirmation,
            AdxAtSignal = signal.AdxAtConfirmation,
            AdxNCandlesAgo = signal.AdxNCandlesAgo,
            IsCatchUp = signal.IsCatchUp,
            PositionOpened = positionOpened,
            PositionId = positionId,
            SkipReason = skipReason,
            CreatedAt = _clock.Now
        };
        await _signalLogRepository.LogAsync(entry, ct);
    }

    private static SharedModels.ConfidenceLevel MapConfidence(ConfidenceLevel confidence) => confidence switch
    {
        ConfidenceLevel.High => SharedModels.ConfidenceLevel.High,
        ConfidenceLevel.Medium => SharedModels.ConfidenceLevel.Medium,
        _ => SharedModels.ConfidenceLevel.Low
    };
}