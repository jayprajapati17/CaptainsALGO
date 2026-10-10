using AlgoWorker.Configuration;
using AlgoWorker.Models;
using Microsoft.Extensions.Options;

namespace AlgoWorker.Services;

/// <summary>
/// Startup replay of 5-min Nifty candles (previous trading days + today so far) into the 5-min stream, which feeds
/// BOTH the ORB engine (so a mid-day restart sees the real 09:15 opening range instead of treating the first live
/// candle as the range) and the Reversal engine (EMA21/50 warm-up, previous-day low, today's low).
/// The caller sets IsSeeding on both engines first, so nothing trades on historical candles.
/// </summary>
public sealed class FiveMinHistoricalSeederService
{
    private readonly UpstoxRestClient _restClient;
    private readonly FiveMinCandleAggregatorService _aggregator;
    private readonly StrategyOptions _strategy;
    private readonly UpstoxOptions _upstox;
    private readonly ILogger<FiveMinHistoricalSeederService> _logger;

    public FiveMinHistoricalSeederService(
        UpstoxRestClient restClient,
        FiveMinCandleAggregatorService aggregator,
        IOptions<StrategyOptions> strategy,
        IOptions<UpstoxOptions> upstox,
        ILogger<FiveMinHistoricalSeederService> logger)
    {
        _restClient = restClient;
        _aggregator = aggregator;
        _strategy = strategy.Value;
        _upstox = upstox.Value;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct)
    {
        var daysToKeep = Math.Max(1, _strategy.ReversalSeedLookbackTradingDays);
        var yesterday = DateOnly.FromDateTime(DateTime.Today).AddDays(-1);
        var from = yesterday.AddDays(-10);

        var fetched = await _restClient.GetHistoricalCandles5MinAsync(_upstox.NiftyInstrumentKey, from, yesterday, ct);
        var recentDates = fetched
            .Select(c => DateOnly.FromDateTime(c.OpenTime.Date))
            .Distinct().OrderByDescending(d => d).Take(daysToKeep).ToHashSet();
        var history = fetched.Where(c => recentDates.Contains(DateOnly.FromDateTime(c.OpenTime.Date))).ToList();

        List<Candle> today;
        try
        {
            // Only fully elapsed candles -- the in-progress one will arrive live.
            today = (await _restClient.GetIntraday5MinCandlesAsync(_upstox.NiftyInstrumentKey, ct))
                .Where(c => c.OpenTime.AddMinutes(5) <= DateTimeOffset.Now)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch today's 5-min candles (normal right at market open). Continuing with history only.");
            today = new List<Candle>();
        }

        var all = history.Concat(today).OrderBy(c => c.OpenTime).ToList();
        foreach (var candle in all)
            _aggregator.SeedClosedCandle(candle);

        _logger.LogInformation("5-min seeding complete -- {Count} candles replayed ({Days} previous day(s) + {Today} today).",
            all.Count, recentDates.Count, today.Count);
    }
}
