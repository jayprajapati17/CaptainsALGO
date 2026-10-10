using AlgoWorker.Configuration;
using AlgoWorker.Models;
using AlgoWorker.Services.Indicators;
using Microsoft.Extensions.Options;

namespace AlgoWorker.Services;

/// <summary>
/// Nifty 50 UPSIDE REVERSAL strategy (5-min, bullish only) -- implements Setup A (Double Bottom) and
/// Setup C (Liquidity Sweep + confirmation window) from the strategy logic document.
/// Feed every CLOSED 5-min candle in chronological order (history first, then live).
/// While <see cref="IsSeeding"/> is true state is built but no signal is ever raised, so a mid-day
/// restart never trades on something that already happened.
/// Filters used: time window, price below EMA21, support zone (previous-day low / today's low),
/// strong-down-trend-day skip, min R:R. Volume / VWAP / VPVR filters are intentionally not used.
/// </summary>
public sealed class ReversalSignalEngine
{
    private const int MaxHistory = 400;
    private const int SweepLookbackForA = 60;   // candles searched for the first bottom
    private const int MinGapBetweenBottoms = 6; // candles (30 min)
    private static readonly TimeSpan CandleLength = TimeSpan.FromMinutes(5);

    private readonly StrategyOptions _o;
    private readonly ILogger<ReversalSignalEngine> _logger;

    private readonly List<Candle> _c = new();
    private readonly List<double> _ema21 = new();
    private readonly List<double> _ema50 = new();
    private readonly EmaCalculator _fast;
    private readonly EmaCalculator _slow;

    private DateOnly _day;
    private decimal _dayOpen;
    private decimal _dayLow;
    private decimal? _prevDayLow;

    private DateTimeOffset? _lastAPivotTime;
    private SweepPending? _sweep;

    public bool IsSeeding { get; set; }
    public event Action<ReversalSignal>? ReversalConfirmed;

    private sealed class SweepPending
    {
        public decimal SigHigh, SigLow, RedHigh, RedLow;
        public int Waited, HigherLows;
    }

    public ReversalSignalEngine(IOptions<StrategyOptions> options, ILogger<ReversalSignalEngine> logger)
    {
        _o = options.Value;
        _logger = logger;
        _fast = new EmaCalculator(Math.Max(2, _o.ReversalEmaFastPeriod));
        _slow = new EmaCalculator(Math.Max(2, _o.ReversalEmaSlowPeriod));
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
        _ema21.Add(_fast.Update((double)candle.Close));
        _ema50.Add(_slow.Update((double)candle.Close));
        if (_c.Count > MaxHistory)
        {
            _c.RemoveAt(0); _ema21.RemoveAt(0); _ema50.RemoveAt(0);
        }

        // Need enough candles for a meaningful EMA50 before trading anything.
        if (_c.Count < Math.Max(_o.ReversalEmaSlowPeriod, 10)) return;

        if (_o.ReversalSetupCEnabled && EvaluateSweep()) return;
        if (_o.ReversalSetupAEnabled) EvaluateDoubleBottom();
    }

    // ------------------------------------------------------------------
    // Setup C -- Liquidity Sweep + confirmation window
    // ------------------------------------------------------------------
    private bool EvaluateSweep()
    {
        var n = _c.Count;
        var cur = _c[n - 1];

        // Step 2: confirmation window for an already-detected signal candle.
        if (_sweep is { } pending)
        {
            pending.Waited++;

            if (cur.Close < pending.SigLow)
            {
                _logger.LogDebug("Sweep setup cancelled: close {Close} below signal low {Low}.", cur.Close, pending.SigLow);
                _sweep = null;
            }
            else
            {
                if (cur.Low > pending.SigLow) pending.HigherLows++;

                if (cur.Close > pending.SigHigh && pending.HigherLows >= _o.ReversalMinHigherLows)
                {
                    _sweep = null;
                    var entry = cur.Close;
                    var sl = pending.SigLow - (decimal)_o.ReversalSlBufferPoints;
                    var t2 = pending.RedHigh;
                    var t1 = (pending.RedHigh + pending.RedLow) / 2m;
                    if (TryBuildAndRaise(ReversalSetup.LiquiditySweep, cur, entry, sl, t1, t2, pending.SigLow))
                        return true;
                }
                else if (pending.Waited >= _o.ReversalConfirmCandles)
                {
                    _logger.LogDebug("Sweep setup expired without confirmation after {N} candles.", pending.Waited);
                    _sweep = null;
                }
            }
        }

        // Step 1: is the current candle a new signal candle? (only if nothing is pending)
        if (_sweep is null && n >= 2)
        {
            var red = _c[n - 2];
            var isRed = red.Close < red.Open;
            var range = cur.High - cur.Low;
            var strongClose = range > 0 && cur.Close >= cur.Low + range / 2m;   // weak close => ignore

            if (isRed && cur.Low < red.Low && cur.Close > red.Close && cur.Close > cur.Open && strongClose
                && PassesContext(cur, cur.Low))
            {
                _sweep = new SweepPending { SigHigh = cur.High, SigLow = cur.Low, RedHigh = red.High, RedLow = red.Low };
                _logger.LogDebug("Liquidity-sweep signal candle at {Time}: low {Low} swept red low {RedLow}; waiting up to {N} candles.",
                    cur.OpenTime, cur.Low, red.Low, _o.ReversalConfirmCandles);
            }
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Setup A -- Double Bottom
    // ------------------------------------------------------------------
    private void EvaluateDoubleBottom()
    {
        var n = _c.Count;
        var cur = _c[n - 1];
        if (cur.Close <= cur.Open) return;                       // trigger candle must be bullish

        var tol = (decimal)_o.ReversalDoubleBottomTolerancePoints;

        // i2 = the second bottom: one of the last 3 candles before the trigger, and the lowest low since 2 candles before it.
        for (var i2 = n - 2; i2 >= Math.Max(2, n - 4); i2--)
        {
            var p2 = _c[i2];
            if (_lastAPivotTime == p2.OpenTime) continue;

            var lowestSince = true;
            for (var k = i2 - 2; k < n; k++)
                if (k != i2 && _c[k].Low < p2.Low) { lowestSince = false; break; }
            if (!lowestSince) continue;

            // Rejection at the second bottom: lower wick >= 30% of range, or a bullish close.
            if (!HasRejection(p2)) continue;

            // Trigger: the current candle is the FIRST close above the rejection candle's high.
            if (cur.Close <= p2.High) continue;
            var earlier = false;
            for (var k = i2 + 1; k < n - 1; k++)
                if (_c[k].Close > p2.High) { earlier = true; break; }
            if (earlier) continue;

            // First bottom: most recent fractal low (>= MinGap candles earlier) within tolerance of the second.
            var i1 = -1;
            for (var k = i2 - MinGapBetweenBottoms; k >= Math.Max(2, i2 - SweepLookbackForA); k--)
            {
                var low = _c[k].Low;
                if (Math.Abs(low - p2.Low) > tol) continue;
                if (!IsFractalLow(k)) continue;
                if (!HasRejection(_c[k])) continue;
                i1 = k;
                break;
            }
            if (i1 < 0) continue;

            var bottom = Math.Min(_c[i1].Low, p2.Low);

            // No close below the double bottom since the first low (that would invalidate it); meaningful bounce between the lows.
            var bounceHigh = 0m;
            var invalid = false;
            for (var k = i1; k < n; k++)
            {
                if (_c[k].Close < bottom) { invalid = true; break; }
                if (k <= i2 && _c[k].High > bounceHigh) bounceHigh = _c[k].High;
            }
            if (invalid) continue;
            if (bounceHigh - Math.Max(_c[i1].Low, p2.Low) < (decimal)_o.ReversalMinBouncePoints) continue;

            if (!PassesContext(cur, bottom)) continue;

            _lastAPivotTime = p2.OpenTime;   // one trigger per double bottom, whether or not it passes the checks below

            var entry = cur.Close;
            var sl = bottom - (decimal)_o.ReversalSlBufferPoints;
            var t2 = bounceHigh;
            var ema21 = (decimal)_ema21[n - 1];
            var t1 = ema21 > entry && ema21 < t2 ? ema21 : entry + (t2 - entry) / 2m;
            TryBuildAndRaise(ReversalSetup.DoubleBottom, cur, entry, sl, t1, t2, bottom);
            return;
        }
    }

    private static bool HasRejection(Candle c)
    {
        var range = c.High - c.Low;
        if (range <= 0) return false;
        var lowerWick = Math.Min(c.Open, c.Close) - c.Low;
        return c.Close > c.Open || lowerWick >= range * 0.3m;
    }

    private bool IsFractalLow(int k)
    {
        for (var j = k - 2; j <= k + 2; j++)
        {
            if (j == k || j < 0 || j >= _c.Count) continue;
            if (_c[j].Low < _c[k].Low) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------
    // Common filters + signal construction
    // ------------------------------------------------------------------

    /// <summary>Context filters: price stretched below EMA21, at a support zone, and not a strong down-trend day.</summary>
    private bool PassesContext(Candle cur, decimal setupLow)
    {
        var n = _c.Count;
        var ema21 = (decimal)_ema21[n - 1];
        var ema50 = (decimal)_ema50[n - 1];

        if (cur.Close >= ema21) return false;

        var tol = (decimal)_o.ReversalSupportTolerancePoints;
        var atSupport = setupLow - _dayLow <= tol
                        || (_prevDayLow is { } pdl && Math.Abs(setupLow - pdl) <= tol);
        if (!atSupport) return false;

        if (_o.ReversalTrendDownPercent > 0 && _dayOpen > 0 && ema21 < ema50)
        {
            var fallPct = (double)((_dayOpen - cur.Close) / _dayOpen) * 100.0;
            if (fallPct >= _o.ReversalTrendDownPercent) return false;
        }

        return true;
    }

    private bool TryBuildAndRaise(ReversalSetup setup, Candle cur, decimal entry, decimal sl, decimal t1, decimal t2, decimal supportLevel)
    {
        var risk = entry - sl;
        if (risk <= 0 || t2 <= entry) return false;
        if (t1 <= entry) t1 = entry + (t2 - entry) / 2m;
        if ((double)((t2 - entry) / risk) < _o.ReversalMinRiskReward)
        {
            _logger.LogInformation("Reversal {Setup} skipped: R:R {Rr:0.00} below minimum {Min:0.0}.",
                setup, (double)((t2 - entry) / risk), _o.ReversalMinRiskReward);
            return false;
        }

        // Time window (entry candle close time).
        var closeTime = TimeOnly.FromDateTime((cur.OpenTime + CandleLength).DateTime);
        if (closeTime < TimeOnly.Parse(_o.ReversalEarliestEntryTime) ||
            TimeOnly.FromDateTime(cur.OpenTime.DateTime) > TimeOnly.Parse(_o.ReversalLastEntryTime))
        {
            _logger.LogDebug("Reversal {Setup} ignored: outside the entry time window.", setup);
            return false;
        }

        var signal = new ReversalSignal(setup, cur.OpenTime, entry, sl, t1, t2, supportLevel);

        if (IsSeeding)
        {
            _logger.LogDebug("(seeding) Reversal {Setup} at {Time} -- historical replay only, not raised.", setup, cur.OpenTime);
            return false;
        }

        _logger.LogInformation("Reversal {Setup} confirmed at {Time}: entry {Entry}, SL {Sl}, T1 {T1}, T2 {T2}.",
            setup, cur.OpenTime, entry, sl, t1, t2);
        ReversalConfirmed?.Invoke(signal);
        return true;
    }
}
