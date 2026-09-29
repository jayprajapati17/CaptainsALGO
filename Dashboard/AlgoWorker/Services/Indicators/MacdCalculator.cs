namespace AlgoWorker.Services.Indicators;

/// <summary>
/// Standard MACD: Fast EMA(12) - Slow EMA(26) = MACD line; Signal line = EMA(9)
/// of the MACD line; Histogram = MACD line - Signal line. Built by composing
/// the existing incremental EmaCalculator -- O(1) per update, no full-history replay.
/// </summary>
public sealed class MacdCalculator
{
    private readonly EmaCalculator _fast = new(12);
    private readonly EmaCalculator _slow = new(26);
    private readonly EmaCalculator _signal = new(9);

    public double MacdLine { get; private set; }
    public double SignalLine { get; private set; }
    public double Histogram { get; private set; }

    /// <summary>True once the Signal line (EMA9 of MACD) has a real value -- i.e. enough candles have been fed.</summary>
    public bool HasValue { get; private set; }

    public void Update(double close)
    {
        var fast = _fast.Update(close);
        var slow = _slow.Update(close);
        MacdLine = fast - slow;
        SignalLine = _signal.Update(MacdLine);
        Histogram = MacdLine - SignalLine;
        HasValue = _signal.HasValue;
    }
}
