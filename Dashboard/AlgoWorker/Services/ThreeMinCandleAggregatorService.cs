using AlgoWorker.Models;

namespace AlgoWorker.Services;

/// <summary>
/// A distinct type (not just another instance of CandleAggregatorService) so
/// it can be registered separately in DI -- this one feeds the 3-Minute MACD
/// strategy, independently of the 15-min (EMA) and 5-min (Breakout) aggregators.
/// All three consume the same underlying Nifty tick stream (see Worker.OnTickReceived).
/// Uses composition (wraps an internal CandleAggregatorService) since that class is sealed.
/// </summary>
public sealed class ThreeMinCandleAggregatorService
{
    private readonly CandleAggregatorService _inner = new(TimeSpan.FromMinutes(3));

    public event Action<Candle>? CandleClosed
    {
        add => _inner.CandleClosed += value;
        remove => _inner.CandleClosed -= value;
    }

    public void OnTick(MarketTick tick) => _inner.OnTick(tick);

    public void ForceCloseCurrentCandle() => _inner.ForceCloseCurrentCandle();

    public void SeedClosedCandle(Candle candle) => _inner.SeedClosedCandle(candle);
}
