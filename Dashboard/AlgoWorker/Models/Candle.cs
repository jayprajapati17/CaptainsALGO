namespace AlgoWorker.Models;

/// <summary>A single 15-minute OHLC candle for the Nifty 50 index.</summary>
public sealed class Candle
{
    /// <summary>Candle bucket start time, e.g. 09:15, 09:30, ... 15:15 IST.</summary>
    public DateTimeOffset OpenTime { get; init; }

    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }

    /// <summary>True once the 15-min bucket window has fully elapsed and no more ticks will be added.</summary>
    public bool IsClosed { get; set; }

    public static Candle StartNew(DateTimeOffset bucketStart, decimal firstPrice)
        => new()
        {
            OpenTime = bucketStart,
            Open = firstPrice,
            High = firstPrice,
            Low = firstPrice,
            Close = firstPrice,
            Volume = 0,
            IsClosed = false
        };

    public void ApplyTick(decimal price, long lastTradedQty)
    {
        if (price > High) High = price;
        if (price < Low) Low = price;
        Close = price;
        Volume += lastTradedQty;
    }
}
