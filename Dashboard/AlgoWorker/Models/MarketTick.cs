namespace AlgoWorker.Models;

/// <summary>
/// Normalized representation of a single price tick, after protobuf decoding.
/// This is the boundary object between the Upstox-specific wire format and
/// the rest of the application -- everything downstream only ever sees this.
/// </summary>
public sealed record MarketTick(
    string InstrumentKey,
    DateTimeOffset Timestamp,
    decimal LastTradedPrice,
    long LastTradedQuantity,
    long TotalVolume);
