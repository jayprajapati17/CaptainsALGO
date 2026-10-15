namespace AlgoWorker.Services;

/// <summary>
/// A pipeline (other than the Nifty <see cref="Worker"/>, which owns the shared WebSocket) that needs instruments on the
/// shared Upstox feed. The Worker subscribes the union of everybody's keys and ignores ticks of keys another consumer
/// owns, so adding a new instrument pipeline never means editing the Worker's tick handler again.
/// </summary>
public interface IMarketFeedConsumer
{
    /// <summary>Instrument keys (indices) this pipeline wants subscribed right now. Empty when the pipeline is switched off.</summary>
    IReadOnlyCollection<string> FeedInstrumentKeys { get; }
}
