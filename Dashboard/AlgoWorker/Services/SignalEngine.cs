using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AlgoWorker.Configuration;
using AlgoWorker.Models;
using AlgoWorker.Services.Indicators;

namespace AlgoWorker.Services;

/// <summary>
/// Implements the backtested entry logic (design doc Section 3.5):
///   1. Detect EMA21/EMA50 crossover.
///   2. Track it for ConfirmationBars candles -- if it reverses before that, ignore (whipsaw).
///   3. At confirmation, check ADX rising for HIGH confidence; otherwise MEDIUM.
///   4. Death Cross always gets LOW confidence (no proven backtest edge either way).
/// One pending crossover is tracked at a time; a fresh opposite crossover while
/// one is pending simply restarts tracking in the new direction.
/// </summary>
public sealed class SignalEngine
{
    private readonly StrategyOptions _options;
    private readonly ILogger<SignalEngine> _logger;

    private SignalDirection? _pendingDirection;
    private int _pendingCandleCount;
    private IndicatorSnapshot? _pendingCrossSnapshot;

    /// <summary>
    /// >>> NEW: set to true while HistoricalSeederService replays past candles.
    /// While true, crossover TRACKING still happens normally (so state carries
    /// over correctly into live trading), but SignalConfirmed is NOT raised --
    /// otherwise every crossover that already happened in history fires a
    /// live alert and opens a virtual position the instant the bot starts.
    /// </summary>
    public bool IsSeeding { get; set; }

    public event Action<TradeSignal>? SignalConfirmed;

    public SignalEngine(IOptions<StrategyOptions> options, ILogger<SignalEngine> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Call once per closed candle, AFTER the IndicatorEngine has processed it.</summary>
    public void OnIndicatorSnapshot(IndicatorEngine engine, IndicatorSnapshot current)
    {
        var previous = engine.NCandlesAgo(1);
        if (previous is null)
            return; // not enough history yet

        var crossedUp = !previous.Ema21AboveEma50 && current.Ema21AboveEma50;
        var crossedDown = previous.Ema21AboveEma50 && !current.Ema21AboveEma50;

        if (crossedUp || crossedDown)
        {
            var direction = crossedUp ? SignalDirection.GoldenCross : SignalDirection.DeathCross;
            StartTracking(direction, current);
            return;
        }

        if (_pendingDirection is null)
            return;

        // Did the pending crossover reverse before confirmation? -> whipsaw, discard.
        var stillInDirection = _pendingDirection == SignalDirection.GoldenCross
            ? current.Ema21AboveEma50
            : !current.Ema21AboveEma50;

        if (!stillInDirection)
        {
            _logger.LogDebug("Pending {Direction} signal reversed before confirmation (whipsaw) at {Time}.",
                _pendingDirection, current.CandleTime);
            ClearPending();
            return;
        }

        _pendingCandleCount++;
        if (_pendingCandleCount < _options.ConfirmationBars)
            return;

        Confirm(engine, current);
    }

    private void StartTracking(SignalDirection direction, IndicatorSnapshot atCross)
    {
        _pendingDirection = direction;
        _pendingCandleCount = 0;
        _pendingCrossSnapshot = atCross;
        _logger.LogInformation("New {Direction} crossover detected at {Time}, tracking for {Bars} candles before confirming.",
            direction, atCross.CandleTime, _options.ConfirmationBars);
    }

    private void Confirm(IndicatorEngine engine, IndicatorSnapshot current)
    {
        var direction = _pendingDirection!.Value;
        var adxNCandlesAgo = engine.NCandlesAgo(_options.ConfirmationBars)?.Adx ?? current.Adx;
        var adxRising = current.Adx > adxNCandlesAgo;

        var confidence = direction switch
        {
            SignalDirection.GoldenCross when adxRising => ConfidenceLevel.High,
            SignalDirection.GoldenCross => ConfidenceLevel.Medium,
            _ => ConfidenceLevel.Low // Death Cross: no proven edge found in backtest, always Low
        };

        if (confidence == ConfidenceLevel.Low && !_options.AlertBothSides)
        {
            _logger.LogInformation("Death Cross confirmed at {Time} but AlertBothSides=false -- suppressing alert.", current.CandleTime);
            ClearPending();
            return;
        }

        // >>> NEW: during historical seeding, log-only -- don't alert on
        // crossovers that already happened in the past.
        if (IsSeeding)
        {
            _logger.LogDebug("(seeding) {Direction} confirmed at {Time} -- not alerting, historical replay only.",
                direction, current.CandleTime);
            ClearPending();
            return;
        }

        var signal = new TradeSignal(
            Direction: direction,
            Confidence: confidence,
            ConfirmedAtCandleTime: current.CandleTime,
            NiftySpotAtConfirmation: current.Close,
            AdxAtConfirmation: current.Adx,
            AdxNCandlesAgo: adxNCandlesAgo);

        _logger.LogInformation("CONFIRMED {Direction} signal ({Confidence} confidence) at {Time}, spot={Spot}, ADX {AdxOld}->{AdxNow}.",
            direction, confidence, current.CandleTime, current.Close, adxNCandlesAgo, current.Adx);

        SignalConfirmed?.Invoke(signal);
        ClearPending();
    }

    private void ClearPending()
    {
        _pendingDirection = null;
        _pendingCandleCount = 0;
        _pendingCrossSnapshot = null;
    }

    /// <summary>
    /// >>> NEW: Call this ONCE, right after historical seeding finishes (not
    /// during the seed replay itself). Looks backward through the indicator
    /// history to find WHEN the current EMA21/50 state last changed, and
    /// whether it has held for at least ConfirmationBars candles (i.e. would
    /// have been "confirmed" if we'd been watching live). If so, returns a
    /// single catch-up TradeSignal describing that -- so the bot can tell you
    /// "here's the last crossover and it's still active" exactly once on
    /// startup, instead of either staying silent or replaying every
    /// historical confirmation as a separate alert.
    /// </summary>
    public TradeSignal? EvaluateCatchUpSignal(IndicatorEngine engine)
    {
        var history = engine.History.ToList(); // chronological order (oldest first)
        if (history.Count < _options.ConfirmationBars + 1)
            return null;

        var current = history[^1];
        var currentState = current.Ema21AboveEma50;

        // Walk backward while the EMA21/50 relationship stays the same as now;
        // stop at the candle where it was last different -- that's the cross point.
        var crossIndex = history.Count - 1;
        for (var i = history.Count - 2; i >= 0; i--)
        {
            if (history[i].Ema21AboveEma50 == currentState)
            {
                crossIndex = i;
            }
            else
            {
                break;
            }
        }

        // The whole seeded window is one single state -- we don't actually know
        // when it started, so don't guess/alert on it.
        if (crossIndex == 0)
        {
            _logger.LogDebug("Catch-up check: current EMA state spans the entire seeded history -- can't identify when it started, not alerting.");
            return null;
        }

        var candlesHeld = history.Count - crossIndex;
        if (candlesHeld < _options.ConfirmationBars)
        {
            _logger.LogDebug("Catch-up check: current EMA state only {Candles} candle(s) old, needs {Needed} to count as confirmed -- will confirm live if it holds.",
                candlesHeld, _options.ConfirmationBars);
            return null; // still building up -- let the normal live path confirm it in a few candles, same as any fresh crossover
        }

        var crossSnapshot = history[crossIndex];
        var direction = currentState ? SignalDirection.GoldenCross : SignalDirection.DeathCross;
        var adxRising = current.Adx > crossSnapshot.Adx;

        var confidence = direction switch
        {
            SignalDirection.GoldenCross when adxRising => ConfidenceLevel.High,
            SignalDirection.GoldenCross => ConfidenceLevel.Medium,
            _ => ConfidenceLevel.Low
        };

        if (confidence == ConfidenceLevel.Low && !_options.AlertBothSides)
        {
            _logger.LogInformation("Catch-up: {Direction} still active since {Time} but AlertBothSides=false -- not alerting.", direction, crossSnapshot.CandleTime);
            return null;
        }

        _logger.LogInformation("Catch-up signal: {Direction} originally crossed at {CrossTime} ({CandlesHeld} candles ago), still active as of {Now}.",
            direction, crossSnapshot.CandleTime, candlesHeld, current.CandleTime);

        return new TradeSignal(
            Direction: direction,
            Confidence: confidence,
            ConfirmedAtCandleTime: crossSnapshot.CandleTime, // the ORIGINAL cross time, for an accurate message
            NiftySpotAtConfirmation: current.Close,          // but current price, for a realistic strike/entry
            AdxAtConfirmation: current.Adx,
            AdxNCandlesAgo: crossSnapshot.Adx,
            IsCatchUp: true);
    }
}