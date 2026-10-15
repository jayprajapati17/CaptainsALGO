using Microsoft.Extensions.Logging;

namespace AlgoWorker.Services;

/// <summary>
/// Finds the real option expiries from Upstox instead of assuming "every Tuesday": it reads the underlying's contract list
/// (all expiries), keeps the distinct dates and caches them for the exchange (IST) day. If Upstox can't be reached or lists
/// nothing, it falls back to the old Tuesday rule in <see cref="ExpiryResolver"/> so signals still get a best-guess expiry.
/// </summary>
public sealed class ExpiryProvider
{
    private readonly UpstoxRestClient _rest;
    private readonly ExpiryResolver _fallback;
    private readonly IClock _clock;
    private readonly ILogger<ExpiryProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateOnly Day, List<DateOnly> Expiries)> _cache = new();

    public ExpiryProvider(UpstoxRestClient rest, ExpiryResolver fallback, IClock clock, ILogger<ExpiryProvider> logger)
    {
        _rest = rest;
        _fallback = fallback;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>All listed expiries (ascending) for the underlying, cached per day. Empty if Upstox returned none.</summary>
    public async Task<IReadOnlyList<DateOnly>> GetExpiriesAsync(string underlyingKey, CancellationToken ct)
    {
        var today = _clock.Today;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(underlyingKey, out var hit) && hit.Day == today)
                return hit.Expiries;

            try
            {
                var contracts = await _rest.GetOptionContractsAsync(underlyingKey, ct);
                var expiries = contracts.Select(c => c.Expiry).Distinct().OrderBy(d => d).ToList();
                if (expiries.Count > 0)
                {
                    _cache[underlyingKey] = (today, expiries);
                    _logger.LogInformation("Expiries for {Instrument}: {Expiries}", underlyingKey,
                        string.Join(", ", expiries.Take(6).Select(e => e.ToString("dd-MMM-yyyy (ddd)"))));
                }
                return expiries;
            }
            catch (UnauthorizedTokenException) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not fetch the expiry list for {Instrument}; using the Tuesday rule.", underlyingKey);
                return Array.Empty<DateOnly>();
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>The nearest listed expiry on or after <paramref name="today"/> (this week's expiry).</summary>
    public async Task<DateOnly> ResolveCurrentAsync(string underlyingKey, DateOnly today, CancellationToken ct)
    {
        var list = await GetExpiriesAsync(underlyingKey, ct);
        var current = list.Where(d => d >= today).Select(d => (DateOnly?)d).FirstOrDefault();
        return current ?? _fallback.ResolveCurrentWeekExpiry(today);
    }

    /// <summary>The listed expiry after the current one (next week's expiry).</summary>
    public async Task<DateOnly> ResolveNextAsync(string underlyingKey, DateOnly today, CancellationToken ct)
    {
        var list = await GetExpiriesAsync(underlyingKey, ct);
        var upcoming = list.Where(d => d >= today).ToList();
        return upcoming.Count >= 2 ? upcoming[1] : _fallback.ResolveNextWeekExpiry(today);
    }
}