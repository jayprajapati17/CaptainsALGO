using AlgoWorker.Models;

namespace AlgoWorker.Services.Indicators;

/// <summary>
/// Wraps MacdCalculator and turns a stream of closed 3-min candles into a
/// stream of MacdSnapshot. Keeps a small rolling history (mirrors
/// IndicatorEngine's pattern) in case a "N candles ago" lookup is ever needed.
/// </summary>
public sealed class MacdEngine
{
    private readonly MacdCalculator _macd = new();
    private readonly LinkedList<MacdSnapshot> _history = new();
    private const int MaxHistoryKept = 200; // ~10 hours of 3-min candles

    public IReadOnlyCollection<MacdSnapshot> History => _history;

    public MacdSnapshot? Latest => _history.Last?.Value;

    /// <summary>Feed one CLOSED 3-min candle in chronological order. Returns null until the Signal line has warmed up.</summary>
    public MacdSnapshot? OnCandleClosed(Candle candle)
    {
        if (!candle.IsClosed)
            throw new InvalidOperationException("Only closed candles should be fed to the MACD engine.");

        _macd.Update((double)candle.Close);
        if (!_macd.HasValue)
            return null; // still warming up (Signal line needs ~9+26 candles of history)

        var snapshot = new MacdSnapshot(
            CandleTime: candle.OpenTime,
            Close: candle.Close,
            MacdLine: _macd.MacdLine,
            SignalLine: _macd.SignalLine,
            Histogram: _macd.Histogram);

        _history.AddLast(snapshot);
        while (_history.Count > MaxHistoryKept)
            _history.RemoveFirst();

        return snapshot;
    }

    public MacdSnapshot? NCandlesAgo(int n)
    {
        if (n < 0 || n >= _history.Count) return null;
        var node = _history.Last;
        for (var i = 0; i < n && node is not null; i++)
            node = node.Previous;
        return node?.Value;
    }
}
