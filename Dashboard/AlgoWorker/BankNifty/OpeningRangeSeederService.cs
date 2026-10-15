using AlgoWorker.Configuration;
using AlgoWorker.Models;
using Microsoft.Extensions.Options;

namespace AlgoWorker.BankNifty;
using AlgoWorker.Services;

/// <summary>
/// Startup replay of today's already-closed 5-min Bank Nifty candles into the 5-min stream, so a mid-day restart
/// still sees the real 09:15 opening range. The caller sets IsSeeding on the engine first, so nothing trades on history.
/// </summary>
public sealed class OpeningRangeSeederService
{
    private readonly UpstoxRestClient _rest;
    private readonly BankNiftyCandleAggregator _aggregator;
    private readonly UpstoxOptions _upstox;
    private readonly ILogger<OpeningRangeSeederService> _logger;

    public OpeningRangeSeederService(UpstoxRestClient rest, BankNiftyCandleAggregator aggregator,
        IOptions<UpstoxOptions> upstox, ILogger<OpeningRangeSeederService> logger)
    {
        _rest = rest; _aggregator = aggregator; _upstox = upstox.Value; _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct)
    {
        List<Candle> today;
        try
        {
            today = (await _rest.GetIntraday5MinCandlesAsync(_upstox.BankNiftyInstrumentKey, ct))
                .Where(c => c.OpenTime.AddMinutes(5) <= DateTimeOffset.UtcNow)
                .OrderBy(c => c.OpenTime)
                .ToList();
        }
        catch (UnauthorizedTokenException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch today's 5-min candles (normal right at market open). The first live candle becomes the range.");
            return;
        }

        foreach (var c in today) _aggregator.SeedClosedCandle(c);
        _logger.LogInformation("Seeded {Count} of today's 5-min candles.", today.Count);
    }
}
