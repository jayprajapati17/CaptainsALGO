using AlgoWorker.Models;

namespace AlgoWorker.Services.Decoding;

/// <summary>
/// Turns a raw binary WebSocket frame from Upstox's Market Data Feed V3 into
/// a normalized <see cref="MarketTick"/>. Kept as an interface so the rest of
/// the app never has to know or care that the wire format is Protobuf.
/// </summary>
public interface IMarketDataDecoder
{
    /// <summary>
    /// Decodes one WebSocket frame. A single frame can contain updates for
    /// several subscribed instruments at once (Upstox multiplexes them), so
    /// this returns every tick found, each carrying its own instrument key.
    /// </summary>
    IReadOnlyList<MarketTick> Decode(ReadOnlySpan<byte> rawFrame);
}
