using AlgoWorker.Configuration;
using AlgoWorker.Models;
using AlgoWorker.Services.Indicators;
using Microsoft.Extensions.Options;

namespace AlgoWorker.Services;

/// <summary>
/// Nifty 50 LIQUIDITY SWEEP (CE) strategy -- 5-min candles, bullish only.
/// Sweep: the PREVIOUS candle is red; the current candle trades below that candle's low and then CLOSES back above that low.
/// A confirmation window follows: within a few candles one candle must close above the sweep candle's high, with at least
/// one higher low, and no candle may close below the sweep candle's low. The entry is the confirmation candle's close and
/// the stop-loss is the previous (red) candle's low.
/// Feed every CLOSED 5-min candle in chronological order (history first, then live). While <see cref="IsSeeding"/> is true
/// state is built but no signal is ever raised, so a mid-day restart never trades on something that already happened.
/// Filters used: time window, strong-down-trend-day skip, and (only for counter-trend entries, EMA short below EMA long) a support zone (previous-day low / today's low).
/// How the trade is HELD (EMA 9 vs EMA 21, trailing stop-loss) is decided by <see cref="ReversalPositionTracker"/>; this class
/// only publishes the EMA values of the last closed candle.
/// </summary>
public sealed class ReversalSignalEngine
{
    private const int MaxHistory = 400;
    private static readonly TimeSpan CandleLength = TimeSpan.FromMinutes(5);

    private readonly IReversalSettings _o;
    private readonly ILogger _logger;

    private readonly List<Candle> _c = new();
    private readonly EmaCalculator _short;
    private readonly EmaCalculator _long;
    private readonly EmaCalculator _trend;
    private double _emaShort, _emaLong, _emaTrend;

    private DateOnly _day;
    private decimal _dayOpen;
    private decimal _dayLow;
    private decimal? _prevDayLow;

    private SweepPending? _sweep;

    public bool IsSeeding { get; set; }
    public event Action<ReversalSignal>? ReversalConfirmed;

    /// <summary>EMA values after the last candle that was fed (used by the position tracker to decide how to hold).</summary>
    public decimal EmaShort => (decimal)_emaShort;
    public decimal EmaLong => (decimal)_emaLong;

    private sealed class SweepPending
    {
        public decimal SigHigh, SigLow, RedLow;
        public int Waited, HigherLows;
    }

    public ReversalSignalEngine(IOptions<StrategyOptions> options, ILogger<ReversalSignalEngine> logger) : this(options.Value, (ILogger)logger) { }

    /// <summary>Generic form: the same engine for any index.</summary>
    public ReversalSignalEngine(IReversalSettings options, ILogger logger)
    {
        _o = options;
        _logger = logger;
        _short = new EmaCalculator(Math.Max(2, _o.ReversalEmaShortPeriod));
        _long = new EmaCalculator(Math.Max(2, _o.ReversalEmaLongPeriod));
        _trend = new EmaCalculator(Math.Max(2, _o.ReversalEmaTrendPeriod));
    }

    public void OnCandleClosed(Candle candle)
    {
        var day = DateOnly.FromDateTime(candle.OpenTime.Date);
        if (day != _day)
        {
            if (_day != default) _prevDayLow = _dayLow;
            _day = day;
            _dayOpen = candle.Open;
            _dayLow = candle.Low;
            _sweep = null;
        }
        else
        {
            _dayLow = Math.Min(_dayLow, candle.Low);
        }

        _c.Add(candle);
        _emaShort = _short.Update((double)candle.Close);
        _emaLong = _long.Update((double)candle.Close);
        _emaTrend = _trend.Update((double)candle.Close);
        if (_c.Count > MaxHistory) _c.RemoveAt(0);

        // Need enough candles for a meaningful trend EMA before trading anything.
        if (_c.Count < Math.Max(_o.ReversalEmaTrendPeriod, 10)) return;

        EvaluateSweep();
    }

    private void EvaluateSweep()
    {
        var n = _c.Count;
        var cur = _c[n - 1];

        // Step 2: confirmation window for an already-detected sweep candle.
        if (_sweep is { } pending)
        {
            pending.Waited++;

            if (cur.Close < pending.SigLow)
            {
                _logger.LogDebug("Sweep setup cancelled: close {Close} below the sweep low {Low}.", cur.Close, pending.SigLow);
                _sweep = null;
            }
            else
            {
                if (cur.Low > pending.SigLow) pending.HigherLows++;

                if (cur.Close > pending.SigHigh && pending.HigherLows >= _o.ReversalMinHigherLows)
                {
                    _sweep = null;
                    var entry = cur.Close;
                    var sl = pending.RedLow - (decimal)_o.ReversalSlBufferPoints;   // the previous (red) candle's low

                    // A counter-trend bounce (EMA short below EMA long, price below EMA long) must happen at a support zone;
                    // a pullback inside an up-trend (EMA short >= EMA long, or price already above EMA long) needs no support zone.
                    bool trendHold = _emaShort >= _emaLong || (double)entry >= _emaLong;
                    if (!trendHold && !AtSupport(pending.SigLow))
                        _logger.LogDebug("Liquidity sweep ignored: counter-trend bounce away from a support zone.");
                    else if (TryBuildAndRaise(cur, entry, sl, pending.SigLow))
                        return;
                }
                else if (pending.Waited >= _o.ReversalConfirmCandles)
                {
                    _logger.LogDebug("Sweep setup expired without confirmation after {N} candles.", pending.Waited);
                    _sweep = null;
                }
            }
        }

        // Step 1: is the current candle a sweep candle? (only if nothing is pending)
        if (_sweep is null && n >= 2)
        {
            var red = _c[n - 2];
            var isRed = red.Close < red.Open;

            // red previous candle + current low BELOW its low + current close back ABOVE that low
            if (isRed && cur.Low < red.Low && cur.Close > red.Low && PassesContext(cur, cur.Low))
            {
                _sweep = new SweepPending { SigHigh = cur.High, SigLow = cur.Low, RedLow = red.Low };
                _logger.LogDebug("Liquidity sweep at {Time}: low {Low} swept the red candle's low {RedLow}; waiting up to {N} candles for confirmation.",
                    cur.OpenTime, cur.Low, red.Low, _o.ReversalConfirmCandles);
            }
        }
    }

    /// <summary>Support zone: the sweep low is within the tolerance of today's low or the previous day's low.</summary>
    private bool AtSupport(decimal setupLow)
    {
        var tol = (decimal)_o.ReversalSupportTolerancePoints;
        return setupLow - _dayLow <= tol
               || (_prevDayLow is { } pdl && Math.Abs(setupLow - pdl) <= tol);
    }

    /// <summary>Context filter: not a strong down-trend day.</summary>
    private bool PassesContext(Candle cur, decimal setupLow)
    {
        var ema21 = (decimal)_emaLong;
        var emaTrend = (decimal)_emaTrend;

        if (_o.ReversalTrendDownPercent > 0 && _dayOpen > 0 && ema21 < emaTrend)
        {
            var fallPct = (double)((_dayOpen - cur.Close) / _dayOpen) * 100.0;
            if (fallPct >= _o.ReversalTrendDownPercent) return false;
        }

        return true;
    }

    private bool TryBuildAndRaise(Candle cur, decimal entry, decimal sl, decimal sweepLow)
    {
        if (entry - sl <= 0)
        {
            _logger.LogInformation("Liquidity sweep skipped: entry {Entry} is not above the stop-loss {Sl}.", entry, sl);
            return false;
        }

        // Time window (entry candle close time).
        var closeTime = TimeOfDay.Of((cur.OpenTime + CandleLength));
        if (closeTime < TimeOfDay.Parse(_o.ReversalEarliestEntryTime) ||
            TimeOfDay.Of(cur.OpenTime) > TimeOfDay.Parse(_o.ReversalLastEntryTime))
        {
            _logger.LogDebug("Liquidity sweep ignored: outside the entry time window.");
            return false;
        }

        var signal = new ReversalSignal(cur.OpenTime, entry, sl, sweepLow, (decimal)_emaShort, (decimal)_emaLong);

        if (IsSeeding)
        {
            _logger.LogDebug("(seeding) Liquidity sweep at {Time} -- historical replay only, not raised.", cur.OpenTime);
            return false;
        }

        _logger.LogInformation("Liquidity sweep confirmed at {Time}: entry {Entry}, SL {Sl}, EMA short {E9:N2}, EMA long {E21:N2} -> {Mode}.",
            cur.OpenTime, entry, sl, signal.EmaShort, signal.EmaLong, signal.HoldWithTrailing ? "hold with trailing SL" : "hold until EMA long");
        ReversalConfirmed?.Invoke(signal);
        return true;
    }
}
