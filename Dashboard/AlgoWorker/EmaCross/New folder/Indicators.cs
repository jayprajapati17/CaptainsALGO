namespace AlgoWorker.EmaCross.Core;

/// <summary>EMA with alpha = 2 / (period + 1), seeded with the first close, never reset (section 2.3 rule 1).</summary>
public sealed class Ema
{
    private readonly double _alpha;
    private double? _value;

    public Ema(int period) => _alpha = 2.0 / (period + 1);

    public double? Value => _value;

    public double Update(double close)
    {
        _value = _value is null ? close : _alpha * close + (1 - _alpha) * _value.Value;
        return _value.Value;
    }
}

/// <summary>
/// Wilder ADX. Seeds TR / +DM / -DM with the sum of the first <c>period</c> bars, then
/// smoothed = previous - previous / period + current; DX = 100 * |+DI - -DI| / (+DI + -DI); ADX is DX
/// seeded with the mean of the first <c>period</c> DX values and Wilder-smoothed afterwards.
/// <see cref="Update"/> returns NaN until the first ADX value exists (2 * period bars).
/// </summary>
public sealed class WilderAdx
{
    private readonly int _period;
    private double _prevHigh, _prevLow, _prevClose;
    private bool _hasPrev;

    private int _bars;                       // bars with a TR / DM so far (excludes the very first bar)
    private double _trSum, _plusSum, _minusSum;
    private double _tr, _plus, _minus;       // smoothed values once seeded
    private int _dxCount;
    private double _dxSum;
    private double _adx = double.NaN;

    public WilderAdx(int period = 14) => _period = period;

    public double Current => _adx;

    public double Update(Bar b) => Update(b.High, b.Low, b.Close);

    public double Update(double high, double low, double close)
    {
        if (!_hasPrev)
        {
            _prevHigh = high; _prevLow = low; _prevClose = close; _hasPrev = true;
            return _adx;
        }

        double upMove = high - _prevHigh;
        double downMove = _prevLow - low;
        double plusDm = upMove > downMove && upMove > 0 ? upMove : 0;
        double minusDm = downMove > upMove && downMove > 0 ? downMove : 0;
        double tr = Math.Max(high - low, Math.Max(Math.Abs(high - _prevClose), Math.Abs(low - _prevClose)));
        _prevHigh = high; _prevLow = low; _prevClose = close;
        _bars++;

        if (_bars <= _period)
        {
            _trSum += tr; _plusSum += plusDm; _minusSum += minusDm;
            if (_bars < _period) return _adx;
            _tr = _trSum; _plus = _plusSum; _minus = _minusSum;
        }
        else
        {
            _tr = _tr - _tr / _period + tr;
            _plus = _plus - _plus / _period + plusDm;
            _minus = _minus - _minus / _period + minusDm;
        }

        double plusDi = _tr == 0 ? 0 : 100.0 * _plus / _tr;
        double minusDi = _tr == 0 ? 0 : 100.0 * _minus / _tr;
        double sum = plusDi + minusDi;
        double dx = sum == 0 ? 0 : 100.0 * Math.Abs(plusDi - minusDi) / sum;

        if (_dxCount < _period)
        {
            _dxSum += dx;
            _dxCount++;
            if (_dxCount == _period) _adx = _dxSum / _period;
        }
        else
        {
            _adx = (_adx * (_period - 1) + dx) / _period;
        }
        return _adx;
    }
}

/// <summary>Builds the CPR for each trading day from the previous day's candles (use one instance per timeframe feed).</summary>
public sealed class CprTracker
{
    private DateOnly? _day;
    private double _h, _l, _c;
    private CprRange? _today;

    /// <summary>Today's CPR (from yesterday's H/L/C). Null on the first day seen (no previous day).</summary>
    public CprRange? Today => _today;

    /// <summary>Feed every candle in time order BEFORE evaluating filters for it.</summary>
    public CprRange? Update(Bar b)
    {
        var d = DateOnly.FromDateTime(b.Time);
        if (_day is null)
        {
            _day = d; _h = b.High; _l = b.Low; _c = b.Close; _today = null;
        }
        else if (d != _day)
        {
            _today = CprRange.FromPreviousDay(_h, _l, _c);
            _day = d; _h = b.High; _l = b.Low; _c = b.Close;
        }
        else
        {
            _h = Math.Max(_h, b.High); _l = Math.Min(_l, b.Low); _c = b.Close;
        }
        return _today;
    }
}
