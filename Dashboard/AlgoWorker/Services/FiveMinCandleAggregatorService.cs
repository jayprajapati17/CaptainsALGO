using AlgoWorker.Models;

namespace AlgoWorker.Services;

/// <summary>
/// A distinct type (not just another instance of CandleAggregatorService) so
/// it can be registered separately in DI -- this one feeds the Previous-Day
/// High/Low Breakout strategy, independently of the 15-min aggregator used
/// by the EMA21/50 strategy. Both consume the same underlying Nifty tick
/// stream (see Worker.OnTickReceived). Uses composition (wraps an internal
/// CandleAggregatorService) rather than inheritance, since
/// CandleAggregatorService is sealed.
/// </summary>
public sealed class FiveMinCandleAggregatorService
{
    private readonly CandleAggregatorService _inner = new(TimeSpan.FromMinutes(5));

    public event Action<Candle>? CandleClosed
    {
        add => _inner.CandleClosed += value;
        remove => _inner.CandleClosed -= value;
    }

    public void OnTick(MarketTick tick) => _inner.OnTick(tick);

    public void ForceCloseCurrentCandle() => _inner.ForceCloseCurrentCandle();

    public void SeedClosedCandle(Candle candle) => _inner.SeedClosedCandle(candle);
}
