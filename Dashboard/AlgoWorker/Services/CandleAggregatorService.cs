using AlgoWorker.Models;

namespace AlgoWorker.Services;

/// <summary>
/// Buckets incoming ticks into 15-minute candles aligned to the NSE session grid
/// (09:15, 09:30, 09:45, ... 15:15, 15:30 IST). Raises <see cref="CandleClosed"/>
/// the moment a bucket's time window has fully elapsed.
/// </summary>
public sealed class CandleAggregatorService
{
    // >>> CHANGED: was a hardcoded `private static readonly TimeSpan
    // BucketSize = TimeSpan.FromMinutes(15);`. Now configurable via
    // constructor, so the same class can also aggregate 5-min candles for
    // the new Previous-Day High/Low Breakout strategy (see
    // FiveMinCandleAggregatorService). Defaults to 15 min to keep the
    // existing DI registration (`AddSingleton<CandleAggregatorService>()`)
    // working unchanged for the EMA strategy's aggregator.
    private readonly TimeSpan _bucketSize;
    private Candle? _current;

    public CandleAggregatorService(TimeSpan? bucketSize = null)
    {
        _bucketSize = bucketSize ?? TimeSpan.FromMinutes(15);
    }

    public event Action<Candle>? CandleClosed;

    /// <summary>Call this for every incoming tick, in chronological order.</summary>
    public void OnTick(MarketTick tick)
    {
        var bucketStart = FloorToBucket(tick.Timestamp);

        if (_current is null)
        {
            _current = Candle.StartNew(bucketStart, tick.LastTradedPrice);
            return;
        }

        if (bucketStart > _current.OpenTime)
        {
            // A new bucket has started -> the previous one is now final.
            CloseCurrentAndStart(bucketStart, tick);
            return;
        }

        _current.ApplyTick(tick.LastTradedPrice, tick.LastTradedQuantity);
    }

    /// <summary>
    /// Called by the market-hours worker right after the session end time,
    /// so the last candle of the day still gets closed and flows through the
    /// indicator pipeline even if no further tick arrives to trigger it naturally.
    /// </summary>
    public void ForceCloseCurrentCandle()
    {
        if (_current is null) return;
        _current.IsClosed = true;
        CandleClosed?.Invoke(_current);
        _current = null;
    }

    /// <summary>Used by the Historical Seeder to replay already-closed candles from the REST API.</summary>
    public void SeedClosedCandle(Candle candle)
    {
        candle.IsClosed = true;
        CandleClosed?.Invoke(candle);
    }

    private void CloseCurrentAndStart(DateTimeOffset newBucketStart, MarketTick tick)
    {
        _current!.IsClosed = true;
        CandleClosed?.Invoke(_current);
        _current = Candle.StartNew(newBucketStart, tick.LastTradedPrice);
    }

    private DateTimeOffset FloorToBucket(DateTimeOffset t)
    {
        var bucketMinutes = (int)_bucketSize.TotalMinutes;
        var minutesSinceMidnight = t.Hour * 60 + t.Minute;
        var flooredMinutes = (minutesSinceMidnight / bucketMinutes) * bucketMinutes;
        var hour = flooredMinutes / 60;
        var minute = flooredMinutes % 60;
        return new DateTimeOffset(t.Year, t.Month, t.Day, hour, minute, 0, t.Offset);
    }
}