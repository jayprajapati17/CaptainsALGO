using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AlgoWorker.Configuration;

namespace AlgoWorker.Services;

/// <summary>
/// Per the user's decision (design doc Section 6, point 5): instead of maintaining
/// a static NSE holiday list, this asks Upstox's live Exchange Status API
/// (GET /v2/market/status/NSE) whether the market is open right now. Holidays,
/// special sessions, etc. are automatically reflected without any manual upkeep.
/// A local time-window check (SessionStartTime/EndTime) is kept as a cheap
/// first-pass filter so we don't hit the API every second outside trading hours.
/// </summary>
public sealed class MarketHoursService
{
    private readonly UpstoxRestClient _restClient;
    private readonly StrategyOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<MarketHoursService> _logger;

    public MarketHoursService(UpstoxRestClient restClient, IOptions<StrategyOptions> options, IClock clock, ILogger<MarketHoursService> logger)
    {
        _restClient = restClient;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Cheap, local-only check: are we within the configured HH:mm window on a weekday? No API call.</summary>
    public bool IsWithinConfiguredWindow(DateTimeOffset now)
    {
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        var start = TimeOnly.Parse(_options.SessionStartTime);
        var end = TimeOnly.Parse(_options.SessionEndTime);
        var nowTime = TimeOnly.FromDateTime(now.DateTime);
        return nowTime >= start && nowTime <= end;
    }

    /// <summary>Authoritative check: calls Upstox's live Exchange Status API. Use sparingly (e.g. once a minute), not per-tick.</summary>
    public async Task<bool> IsMarketOpenAsync(CancellationToken ct)
    {
        try
        {
            var (state, _) = await _restClient.GetMarketStatusAsync(ct);
            var isOpen = state == ExchangeMarketState.NormalOpen;
            _logger.LogDebug("Upstox exchange status: {State} (open={Open})", state, isOpen);
            return isOpen;
        }
        catch (UnauthorizedTokenException)
        {
            throw; // let the caller handle token-expiry specifically
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reach Upstox exchange-status API -- falling back to local time window.");
            return IsWithinConfiguredWindow(_clock.Now);
        }
    }
}