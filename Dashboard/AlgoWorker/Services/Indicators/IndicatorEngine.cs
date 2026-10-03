using AlgoWorker.Configuration;
using AlgoWorker.Models;
using Microsoft.Extensions.Options;

namespace AlgoWorker.Services.Indicators;

/// <summary>
/// Wraps the EMA10/21/50 + ADX/+DI/-DI calculators and turns a stream of
/// closed candles into a stream of IndicatorSnapshot. Also keeps a small
/// rolling window of recent snapshots since the Signal Engine and the
/// Virtual Position Tracker's exit logic both need "N candles ago" lookups.
/// </summary>
public sealed class IndicatorEngine
{
    private readonly EmaCalculator _ema10;
    private readonly EmaCalculator _ema21;
    private readonly EmaCalculator _ema50;
    private readonly AdxCalculator _adx;

    private readonly LinkedList<IndicatorSnapshot> _history = new();
    private const int MaxHistoryKept = 200; // plenty of candles for all lookbacks, whatever the configured timeframe

    public IndicatorEngine(IOptions<StrategyOptions> options)
    {
        var o = options.Value;
        _ema10 = new EmaCalculator(o.EmaFastPeriod);
        _ema21 = new EmaCalculator(o.EmaMidPeriod);
        _ema50 = new EmaCalculator(o.EmaSlowPeriod);
        _adx = new AdxCalculator(o.AdxPeriod);
    }

    public IReadOnlyCollection<IndicatorSnapshot> History => _history;

    public IndicatorSnapshot? Latest => _history.Last?.Value;

    /// <summary>Feed one CLOSED candle in chronological order. Returns the resulting snapshot.</summary>
    public IndicatorSnapshot OnCandleClosed(Candle candle)
    {
        if (!candle.IsClosed)
            throw new InvalidOperationException("Only closed candles should be fed to the indicator engine.");

        var closeD = (double)candle.Close;
        var ema10 = _ema10.Update(closeD);
        var ema21 = _ema21.Update(closeD);
        var ema50 = _ema50.Update(closeD);
        _adx.Update(candle.High, candle.Low, candle.Close);

        var snapshot = new IndicatorSnapshot(
            CandleTime: candle.OpenTime,
            Close: candle.Close,
            Ema10: ema10,
            Ema21: ema21,
            Ema50: ema50,
            Adx: _adx.Adx,
            PlusDi: _adx.PlusDi,
            MinusDi: _adx.MinusDi);

        _history.AddLast(snapshot);
        while (_history.Count > MaxHistoryKept)
            _history.RemoveFirst();

        return snapshot;
    }

    /// <summary>Get the snapshot N closed candles before the latest one (null if not enough history yet).</summary>
    public IndicatorSnapshot? NCandlesAgo(int n)
    {
        if (n < 0 || n >= _history.Count) return null;
        var node = _history.Last;
        for (var i = 0; i < n && node is not null; i++)
            node = node.Previous;
        return node?.Value;
    }
}
