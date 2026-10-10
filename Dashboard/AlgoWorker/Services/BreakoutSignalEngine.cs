using AlgoWorker.Models;

namespace AlgoWorker.Services;

/// <summary>
/// >>> CHANGED: this used to be a "Previous-Day High/Low Breakout" strategy
/// (reference levels came from yesterday's actual High/Low, fetched via
/// SetPreviousDayLevels()). Per the user's updated request, it is now an
/// "Opening Range Breakout" (ORB) strategy: the reference High/Low are
/// TODAY's own first 5-min candle (09:15-09:20 IST) -- the very first candle
/// of each trading day defines the range, and every candle after it is
/// checked against that range's High/Low on CLOSE.
///
/// Re-entry: each side (UP / DOWN) can fire again after a candle has closed back inside the range
/// and then closes outside it once more (see RE-ARM below). Late-start/restart catch-up is unchanged:
/// a breakout already in progress at startup never trades.
///
/// Day-boundary detection is automatic: whenever a candle's date differs from
/// the last one seen, that candle becomes the new day's opening-range candle
/// and all "already triggered today" flags reset. No external SetXxxLevels
/// call is needed anymore.
/// </summary>
public sealed class BreakoutSignalEngine
{
    private readonly ILogger<BreakoutSignalEngine> _logger;

    private decimal? _openingRangeHigh;
    private decimal? _openingRangeLow;
    private bool _upTriggeredToday;
    private bool _downTriggeredToday;
    private DateOnly _currentDay;

    /// <summary>
    /// >>> NEW: set to true while today's already-elapsed 5-min candles are
    /// being replayed at startup (see Worker.EnsureSeededForTodayAsync). While
    /// true, the opening range is still captured correctly and trigger flags
    /// still update, but BreakoutConfirmed is NOT raised for any breakout that
    /// already happened earlier today -- instead it's stashed and can be
    /// retrieved once, after seeding, via <see cref="ConsumeCatchUpSignal"/>.
    /// This mirrors SignalEngine.IsSeeding/EvaluateCatchUpSignal for the EMA
    /// strategy, so a mid-day bot restart behaves sensibly here too.
    /// </summary>
    public bool IsSeeding { get; set; }

    private BreakoutSignal? _catchUpSignal;

    public event Action<BreakoutSignal>? BreakoutConfirmed;

    public BreakoutSignalEngine(ILogger<BreakoutSignalEngine> logger)
    {
        _logger = logger;
    }

    /// <summary>Feed every CLOSED 5-min candle here, in chronological order.</summary>
    public void OnCandleClosed(Candle candle)
    {
        var candleDay = DateOnly.FromDateTime(candle.OpenTime.Date);

        if (_currentDay != candleDay)
        {
            // First candle of a new trading day -- THIS candle IS the opening
            // range. It can't be a breakout of its own range, so just capture
            // it and reset the day's trigger flags.
            _currentDay = candleDay;
            _openingRangeHigh = candle.High;
            _openingRangeLow = candle.Low;
            _upTriggeredToday = false;
            _downTriggeredToday = false;
            _catchUpSignal = null;

            _logger.LogInformation(
                "Opening range captured for {Day} from first 5-min candle ({Time}): High={High}, Low={Low}",
                candleDay, candle.OpenTime, candle.High, candle.Low);
            return;
        }

        if (_openingRangeHigh is null || _openingRangeLow is null)
        {
            _logger.LogDebug("Breakout check skipped -- opening range not captured yet for {Day}.", candleDay);
            return;
        }

        // RE-ARM: a breakout side is "armed" again once a candle closes back inside the range
        // (at/below High re-arms UP; at/above Low re-arms DOWN). So after a reversal / stop-loss,
        // a NEW close outside the range is treated as a fresh breakout and can trade again.
        // While price just stays outside the range without ever closing back inside, no repeat signal fires.
        if (candle.Close <= _openingRangeHigh.Value) _upTriggeredToday = false;
        if (candle.Close >= _openingRangeLow.Value) _downTriggeredToday = false;

        if (!_upTriggeredToday && candle.Close > _openingRangeHigh.Value)
        {
            _upTriggeredToday = true;
            var signal = new BreakoutSignal(BreakoutDirection.Up, candle.OpenTime, candle.Close, _openingRangeHigh.Value);
            RaiseOrStash(signal, "UP", _openingRangeHigh.Value);
            return;
        }

        if (!_downTriggeredToday && candle.Close < _openingRangeLow.Value)
        {
            _downTriggeredToday = true;
            var signal = new BreakoutSignal(BreakoutDirection.Down, candle.OpenTime, candle.Close, _openingRangeLow.Value);
            RaiseOrStash(signal, "DOWN", _openingRangeLow.Value);
        }
    }

    private void RaiseOrStash(BreakoutSignal signal, string label, decimal level)
    {
        if (IsSeeding)
        {
            _catchUpSignal = signal;
            _logger.LogDebug("(seeding) Opening-range BREAKOUT {Label} at {Time}: close vs level {Level} -- not alerting yet, historical replay only.",
                label, signal.ConfirmedAtCandleTime, level);
            return;
        }

        _logger.LogInformation("Opening-range BREAKOUT {Label} confirmed at {Time}: close {Close} vs level {Level}",
            label, signal.ConfirmedAtCandleTime, signal.NiftySpotAtConfirmation, level);
        BreakoutConfirmed?.Invoke(signal);
    }

    /// <summary>
    /// >>> NEW: call once, right after replaying today's already-elapsed 5-min
    /// candles at startup. Returns a signal if a breakout ALREADY happened
    /// earlier today (and hasn't been alerted on yet), so a mid-day restart
    /// doesn't silently miss it. Returns null if nothing triggered yet today.
    /// </summary>
    public BreakoutSignal? ConsumeCatchUpSignal()
    {
        var signal = _catchUpSignal;
        _catchUpSignal = null;
        return signal;
    }
}