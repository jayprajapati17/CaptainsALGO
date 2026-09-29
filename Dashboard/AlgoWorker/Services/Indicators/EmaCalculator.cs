namespace AlgoWorker.Services.Indicators;

/// <summary>
/// Incremental exponential moving average. O(1) per update -- no need to
/// replay the whole candle history on every new candle.
/// </summary>
public sealed class EmaCalculator
{
    private readonly int _period;
    private readonly double _k;
    private double? _value;

    public EmaCalculator(int period)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _k = 2.0 / (period + 1);
    }

    public bool HasValue => _value.HasValue;

    public double Value => _value ?? throw new InvalidOperationException(
        "EMA has no value yet -- call Seed() or Update() at least once.");

    /// <summary>First value ever fed to the EMA becomes its seed (simple, common approach).</summary>
    public double Update(double close)
    {
        _value = _value is null ? close : (close * _k) + (_value.Value * (1 - _k));
        return _value.Value;
    }

    /// <summary>Explicitly seed with a precomputed value (e.g. SMA of the first N closes) for a cleaner warm-up.</summary>
    public void Seed(double value) => _value = value;
}
