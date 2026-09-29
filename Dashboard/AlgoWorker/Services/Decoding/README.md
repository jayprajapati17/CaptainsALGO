# Protobuf decoding -- 1 manual step required

Upstox's Market Data Feed **V3** sends every WebSocket message as a **Protobuf-encoded
binary frame**, decoded using a `.proto` schema file that **Upstox owns and updates
periodically** (they even pushed a breaking schema update in early 2025 with an
"urgent update" notice to all developers). Because that file can change and I don't
have a reliable way to confirm the exact current field numbers from here, I have
**not** hand-written a guessed schema into this project -- doing so risks silently
decoding garbage (wrong field = wrong price = wrong signal = real money risk).

Instead, `ProtobufMarketDataDecoder` in this folder is a thin, correctly-structured
wrapper that expects the **official generated classes**. Here is the one-time setup:

## Steps

1. Go to the Upstox Developer Docs -> **Market Data Feed V3** page:
   `https://upstox.com/developer/api-documentation/v3/get-market-data-feed/`
   and download the current **`MarketDataFeedV3.proto`** file (there's a download
   link on that page, and Upstox's GitHub orgs like `upstox/upstox-python` /
   `upstox/upstox-java` also mirror it under `examples/websocket/market_data/v3/`).

2. Drop that file into the project root as `MarketDataFeedV3.proto`.

3. In `NiftyEmaAlertBot.csproj`, uncomment this line (it's already there, commented out):
   ```xml
   <Protobuf Include="MarketDataFeedV3.proto" />
   ```
   The `Grpc.Tools` package (already referenced) will auto-generate the C# message
   classes (e.g. `FeedResponse`, `Ltpc`, `MarketFullFeed`, etc. -- exact names depend
   on Upstox's current schema) into `obj/` at build time.

4. Open `ProtobufMarketDataDecoder.cs` and fill in the `Decode` body using the
   generated `FeedResponse` class -- something like:
   ```csharp
   var response = FeedResponse.Parser.ParseFrom(rawFrame.ToArray());
   var ticks = new List<MarketTick>();
   foreach (var (instrumentKey, feed) in response.Feeds)
   {
       var ltpc = feed.Ltpc; // exact property path depends on the current proto shape
       ticks.Add(new MarketTick(instrumentKey, DateTimeOffset.UtcNow, (decimal)ltpc.Ltp, (long)ltpc.Ltq, (long)feed.Vtt));
   }
   return ticks;
   ```
   The generated class's IntelliSense will show you exactly what fields exist in
   *your* downloaded version of the schema -- that's the safest source of truth.
   A single frame can contain updates for *multiple* subscribed instruments at
   once (the bot subscribes to both the Nifty index and, while a virtual
   position is open, the option contract's instrument key), so `Decode` returns
   a list, each tick tagged with its own instrument key.

## Why I didn't just guess

Every Upstox community thread I found (and there are several) about V3 feed
integration issues traces back to a proto-file mismatch. A wrong field mapping
here wouldn't throw a compile error -- it would quietly feed the Indicator Engine
wrong prices, which then feeds wrong EMA/ADX values, which then fires wrong trade
alerts. That's a worse failure mode than "you have to do one manual download step".
