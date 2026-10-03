using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AlgoWorker.Configuration;
using AlgoWorker.Models;

namespace AlgoWorker.Services;

/// <summary>
/// Runs once at startup: pulls ~20 trading days of 15-min candles so EMA50/ADX
/// are already stable by the time the first live tick arrives (a fresh EMA50
/// takes ~50 candles / ~2 trading days to converge, which we don't want to
/// wait through live every single morning).
/// </summary>
public sealed class HistoricalSeederService
{
    private readonly UpstoxRestClient _restClient;
    private readonly CandleAggregatorService _aggregator;
    private readonly StrategyOptions _strategy;
    private readonly UpstoxOptions _upstox;
    private readonly IClock _clock;
    private readonly ILogger<HistoricalSeederService> _logger;

    public HistoricalSeederService(
        UpstoxRestClient restClient,
        CandleAggregatorService aggregator,
        IOptions<StrategyOptions> strategy,
        IOptions<UpstoxOptions> upstox,
        IClock clock,
        ILogger<HistoricalSeederService> logger)
    {
        _restClient = restClient;
        _aggregator = aggregator;
        _strategy = strategy.Value;
        _upstox = upstox.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct)
    {
        // >>> CHANGED (2nd fix): Upstox's V3 Historical Candle API caps 1-15
        // minute interval requests to a MAX 1-MONTH date range per call (see
        // their docs' "Max retrieval record limit" table). The previous version
        // of this method asked for SeedLookbackTradingDays*2 (=40) calendar days,
        // which exceeds that cap and caused a 400 "Invalid date range" error.
        // Fix: hard-clamp the lookback window to 27 calendar days (safely under
        // the 30ish-day limit) regardless of the configured trading-day count.
        // 27 calendar days is still ~18-19 trading days / ~450+ fifteen-min
        // candles -- more than enough for EMA50/ADX to fully converge.
        const int maxSafeLookbackCalendarDays = 27;
        var requestedLookbackDays = _strategy.SeedLookbackTradingDays * 2;
        var lookbackDays = Math.Min(requestedLookbackDays, maxSafeLookbackCalendarDays);

        if (requestedLookbackDays > maxSafeLookbackCalendarDays)
        {
            _logger.LogWarning(
                "Configured SeedLookbackTradingDays ({Configured}) implies a {Requested}-day window, " +
                "which exceeds Upstox's 1-month limit for 15-min candles. Clamping to {Clamped} days.",
                _strategy.SeedLookbackTradingDays, requestedLookbackDays, lookbackDays);
        }

        var yesterday = _clock.Today.AddDays(-1);
        var from = yesterday.AddDays(-lookbackDays);

        _logger.LogInformation("Seeding historical candles from {From} to {To}...", from, yesterday);
        var historicalCandles = await _restClient.GetHistoricalCandlesAsync(_upstox.NiftyInstrumentKey, from, yesterday, _strategy.EmaCandleMinutes, ct);

        // >>> NEW: separate call for today's candles (Historical API rejects a
        // to_date of today -- see UpstoxRestClient.GetIntradayCandlesAsync).
        List<Candle> todaysCandles;
        try
        {
            todaysCandles = await _restClient.GetIntradayCandlesAsync(_upstox.NiftyInstrumentKey, _strategy.EmaCandleMinutes, ct);
        }
        catch (Exception ex)
        {
            // Not fatal -- e.g. this can legitimately return nothing/error right at
            // market open before today has any closed 15-min bar yet.
            _logger.LogWarning(ex, "Could not fetch today's intraday candles (may be normal at/near market open). Continuing with historical data only.");
            todaysCandles = new List<Candle>();
        }

        // >>> NEW: merge both lists in chronological order before replaying them.
        var allCandles = historicalCandles.Concat(todaysCandles).OrderBy(c => c.OpenTime).ToList();

        foreach (var candle in allCandles)
            _aggregator.SeedClosedCandle(candle);

        _logger.LogInformation("Seeding complete -- {Count} candles replayed through the indicator pipeline ({Hist} historical + {Today} today).",
            allCandles.Count, historicalCandles.Count, todaysCandles.Count);
    }
}