using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AlgoWorker.Configuration;
using AlgoWorker.Models;

namespace AlgoWorker.Services;

/// <summary>
/// Runs once at startup (mirrors HistoricalSeederService, but for the MACD
/// strategy's own 3-min candle stream): replays the previous
/// MacdSeedLookbackTradingDays (default 2) trading days' worth of 3-min
/// candles, plus today's so-far candles, so MACD(12,26,9) is already warmed
/// up (the Signal line needs ~35 candles) by the time the first live tick
/// arrives, instead of the bot sitting out the first ~1h45m of every session.
/// </summary>
public sealed class MacdHistoricalSeederService
{
    private readonly UpstoxRestClient _restClient;
    private readonly ThreeMinCandleAggregatorService _aggregator;
    private readonly StrategyOptions _strategy;
    private readonly UpstoxOptions _upstox;
    private readonly ILogger<MacdHistoricalSeederService> _logger;

    public MacdHistoricalSeederService(
        UpstoxRestClient restClient,
        ThreeMinCandleAggregatorService aggregator,
        IOptions<StrategyOptions> strategy,
        IOptions<UpstoxOptions> upstox,
        ILogger<MacdHistoricalSeederService> logger)
    {
        _restClient = restClient;
        _aggregator = aggregator;
        _strategy = strategy.Value;
        _upstox = upstox.Value;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct)
    {
        var tradingDaysToKeep = Math.Max(1, _strategy.MacdSeedLookbackTradingDays);

        // Wide calendar-day buffer so we still get the last N trading days even
        // across a long weekend/holiday cluster; well under Upstox's 1-month cap either way.
        var yesterday = DateOnly.FromDateTime(DateTime.Today).AddDays(-1);
        var from = yesterday.AddDays(-10);

        _logger.LogInformation("Seeding MACD 3-min candles: fetching {From} to {To}, keeping the last {Days} trading day(s)...",
            from, yesterday, tradingDaysToKeep);

        var fetched = await _restClient.GetHistoricalCandles3MinAsync(_upstox.NiftyInstrumentKey, from, yesterday, ct);

        // Keep only the most recent `tradingDaysToKeep` DISTINCT trading dates
        // (the fetch window is deliberately wider than that to survive holidays).
        var recentDates = fetched
            .Select(c => DateOnly.FromDateTime(c.OpenTime.Date))
            .Distinct()
            .OrderByDescending(d => d)
            .Take(tradingDaysToKeep)
            .ToHashSet();

        var historicalCandles = fetched
            .Where(c => recentDates.Contains(DateOnly.FromDateTime(c.OpenTime.Date)))
            .OrderBy(c => c.OpenTime)
            .ToList();

        List<Candle> todaysCandles;
        try
        {
            todaysCandles = await _restClient.GetIntraday3MinCandlesAsync(_upstox.NiftyInstrumentKey, ct);
        }
        catch (Exception ex)
        {
            // Not fatal -- e.g. normal right at market open before today has any closed 3-min bar yet.
            _logger.LogWarning(ex, "Could not fetch today's 3-min intraday candles (may be normal at/near market open). Continuing with historical data only.");
            todaysCandles = new List<Candle>();
        }

        var allCandles = historicalCandles.Concat(todaysCandles).OrderBy(c => c.OpenTime).ToList();

        foreach (var candle in allCandles)
            _aggregator.SeedClosedCandle(candle);

        _logger.LogInformation("MACD seeding complete -- {Count} 3-min candles replayed ({Days} trading day(s) + {Today} today).",
            allCandles.Count, recentDates.Count, todaysCandles.Count);
    }
}
