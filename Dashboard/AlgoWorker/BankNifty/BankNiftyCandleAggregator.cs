using AlgoWorker.Models;
using AlgoWorker.Services;

namespace AlgoWorker.BankNifty;

/// <summary>
/// The Bank Nifty index's own 5-minute candle stream. A distinct type (not a second CandleAggregatorService instance)
/// so it can be registered separately in DI next to the Nifty aggregators -- same wrapper pattern as FiveMinCandleAggregatorService.
/// </summary>
public sealed class BankNiftyCandleAggregator
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
