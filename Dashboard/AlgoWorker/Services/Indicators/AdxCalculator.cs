namespace AlgoWorker.Services.Indicators;

/// <summary>
/// Wilder's ADX / +DI / -DI, updated incrementally one candle at a time.
/// Standard 14-period smoothing (matches what the Upstox-exported columns used
/// in the backtest files were computed with).
/// </summary>
public sealed class AdxCalculator
{
    private readonly int _period;

    private decimal? _prevHigh, _prevLow, _prevClose;
    private double? _smoothedTr, _smoothedPlusDm, _smoothedMinusDm;
    private double? _adx;
    private readonly Queue<double> _dxHistory = new();

    public double PlusDi { get; private set; }
    public double MinusDi { get; private set; }
    public double Adx => _adx ?? 0.0;
    public bool HasValue => _adx.HasValue;

    public AdxCalculator(int period = 14)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
    }

    public void Update(decimal high, decimal low, decimal close)
    {
        if (_prevHigh is null)
        {
            _prevHigh = high;
            _prevLow = low;
            _prevClose = close;
            return;
        }

        var upMove = (double)(high - _prevHigh.Value);
        var downMove = (double)(_prevLow!.Value - low);

        var plusDm = (upMove > downMove && upMove > 0) ? upMove : 0.0;
        var minusDm = (downMove > upMove && downMove > 0) ? downMove : 0.0;

        var trueRange = Math.Max(
            (double)(high - low),
            Math.Max(
                Math.Abs((double)(high - _prevClose!.Value)),
                Math.Abs((double)(low - _prevClose.Value))));

        if (_smoothedTr is null)
        {
            // First period: plain accumulation, Wilder-style seed.
            _smoothedTr = trueRange;
            _smoothedPlusDm = plusDm;
            _smoothedMinusDm = minusDm;
        }
        else
        {
            _smoothedTr = _smoothedTr - (_smoothedTr.Value / _period) + trueRange;
            _smoothedPlusDm = _smoothedPlusDm - (_smoothedPlusDm.Value / _period) + plusDm;
            _smoothedMinusDm = _smoothedMinusDm - (_smoothedMinusDm.Value / _period) + minusDm;
        }

        PlusDi = _smoothedTr > 0 ? 100.0 * (_smoothedPlusDm!.Value / _smoothedTr.Value) : 0.0;
        MinusDi = _smoothedTr > 0 ? 100.0 * (_smoothedMinusDm!.Value / _smoothedTr.Value) : 0.0;

        var diSum = PlusDi + MinusDi;
        var dx = diSum > 0 ? 100.0 * Math.Abs(PlusDi - MinusDi) / diSum : 0.0;

        if (_adx is null)
        {
            _dxHistory.Enqueue(dx);
            if (_dxHistory.Count == _period)
            {
                _adx = _dxHistory.Average();
                _dxHistory.Clear();
            }
        }
        else
        {
            _adx = ((_adx.Value * (_period - 1)) + dx) / _period;
        }

        _prevHigh = high;
        _prevLow = low;
        _prevClose = close;
    }
}
