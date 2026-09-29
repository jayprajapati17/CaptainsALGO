using AlgoWorker.Models;
// The proto's `Type` enum collides by name with System.Type (brought in via
// ImplicitUsings). Alias it so `ProtoFeedType.MarketInfo` etc. is unambiguous.
using ProtoFeedType = Com.Upstox.Marketdatafeederv3Udapi.Rpc.Proto.Type;
// >>> The .proto has no explicit `option csharp_namespace`, so protoc derives
// the C# namespace from `package com.upstox.marketdatafeederv3udapi.rpc.proto`
// by upper-casing the first letter of each dot-separated segment. If the
// namespace below doesn't resolve/build, look inside
// obj/Debug/net8.0/Protos/ or obj/.../Generated/ for the actual generated
// .cs file and copy the exact `namespace ...;` line from there instead.
using ProtoNs = Com.Upstox.Marketdatafeederv3Udapi.Rpc.Proto;

namespace AlgoWorker.Services.Decoding;

/// <summary>
/// Decodes Upstox Market Data Feed V3 Protobuf frames, per the schema in
/// MarketDataFeedV3.proto (project root). Subscriptions are made in "full"
/// mode (see UpstoxWebSocketClient), so live price data for equities/indices
/// arrives nested at Feed.FullFeed.IndexFF/MarketFF.Ltpc rather than
/// top-level Feed.Ltpc (that shortcut is only populated in "ltpc" mode).
/// </summary>
public sealed class ProtobufMarketDataDecoder : IMarketDataDecoder
{
    private readonly ILogger<ProtobufMarketDataDecoder> _logger;
    private bool _loggedFirstFrame;

    public ProtobufMarketDataDecoder(ILogger<ProtobufMarketDataDecoder> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<MarketTick> Decode(ReadOnlySpan<byte> rawFrame)
    {
        ProtoNs.FeedResponse response;
        try
        {
            response = ProtoNs.FeedResponse.Parser.ParseFrom(rawFrame.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse a WebSocket frame as FeedResponse -- skipping this frame.");
            return Array.Empty<MarketTick>();
        }

        if (!_loggedFirstFrame)
        {
            _logger.LogInformation("First WebSocket frame decoded successfully. Type={Type}", response.Type);
            _loggedFirstFrame = true;
        }

        // The very first message on a fresh connection is "market_info"
        // (NSE/BSE/etc. segment open/closed status) -- not a price tick.
        if (response.Type == ProtoFeedType.MarketInfo)
            return Array.Empty<MarketTick>();

        var ticks = new List<MarketTick>(response.Feeds.Count);

        foreach (var (instrumentKey, feed) in response.Feeds)
        {
            // Nifty 50 is an INDEX (uses IndexFF), option contracts are
            // equity-derivative instruments (use MarketFF) -- try both.
            var ltpc = feed.FullFeed?.IndexFF?.Ltpc
                       ?? feed.FullFeed?.MarketFF?.Ltpc
                       ?? feed.Ltpc; // fallback if ever subscribed in "ltpc" mode

            if (ltpc is null)
            {
                _logger.LogDebug("No LTPC in feed for {InstrumentKey} (FeedUnion case: {Case}) -- skipping.",
                    instrumentKey, feed.FeedUnionCase);
                continue;
            }

            var volume = feed.FullFeed?.MarketFF?.Vtt ?? 0L; // indices don't carry a traded volume

            ticks.Add(new MarketTick(
                InstrumentKey: instrumentKey,
                Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(response.CurrentTs),
                LastTradedPrice: (decimal)ltpc.Ltp,
                LastTradedQuantity: ltpc.Ltq,
                TotalVolume: volume));
        }

        return ticks;
    }
}