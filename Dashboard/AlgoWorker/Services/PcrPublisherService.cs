using AlgoData.Models;
using AlgoWorker.Configuration;
using AlgoWorker.Hubs;
using AlgoWorker.Services.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AlgoWorker.Services;

/// <summary>
/// Reads the option chain of Nifty (current weekly expiry) and Bank Nifty (current expiry) every <see cref="PcrOptions.PollSeconds"/>
/// during market hours, works out the put/call ratios, saves a <see cref="PcrSnapshotEntity"/> and pushes "PcrChanged" to the
/// Live page. Pure information -- no strategy uses it, and a failure here (no token yet, Upstox hiccup, DB down) only
/// skips that round: the service never stops and can never affect trading.
/// </summary>
public sealed class PcrPublisherService : BackgroundService
{
    private static readonly TimeSpan MarketOpen = new(9, 15, 0);
    private static readonly TimeSpan MarketClose = new(15, 30, 0);

    private readonly UpstoxRestClient _rest;
    private readonly ExpiryProvider _expiries;
    private readonly IPcrRepository _repo;
    private readonly IHubContext<PositionHub> _hub;
    private readonly UpstoxOptions _upstox;
    private readonly PcrOptions _options;
    private readonly ILogger<PcrPublisherService> _logger;

    // First near-ATM PCR of the day per index, so the page can show how far it has moved since the open.
    private readonly Dictionary<string, (DateOnly Day, double? Pcr)> _dayOpen = new();
    private DateTimeOffset _lastWarning = DateTimeOffset.MinValue;

    public PcrPublisherService(UpstoxRestClient rest, ExpiryProvider expiries, IPcrRepository repo,
        IHubContext<PositionHub> hub, IOptions<UpstoxOptions> upstox, IOptions<PcrOptions> options,
        ILogger<PcrPublisherService> logger)
    {
        _rest = rest; _expiries = expiries; _repo = repo; _hub = hub;
        _upstox = upstox.Value; _options = options.Value; _logger = logger;
    }

    private IEnumerable<(string Underlying, string Key, string Name)> Indices()
    {
        yield return (Underlyings.Nifty50, _upstox.NiftyInstrumentKey, "Nifty");
        yield return (Underlyings.BankNifty, _upstox.BankNiftyInstrumentKey, "Bank Nifty");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();   // let the host finish starting (settings seeded, schema migrated) first
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.Enabled && IsMarketHours(DateTimeOffset.UtcNow))
                    await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Once every 10 minutes at most -- a missing token would otherwise log every poll.
                if (DateTimeOffset.UtcNow - _lastWarning > TimeSpan.FromMinutes(10))
                {
                    _lastWarning = DateTimeOffset.UtcNow;
                    _logger.LogWarning(ex, "PCR update failed -- will retry.");
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, _options.PollSeconds)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Monday-Friday, 09:15-15:30 IST. Exchange holidays are not known here; on a holiday the chain simply doesn't change.</summary>
    public static bool IsMarketHours(DateTimeOffset utcNow)
    {
        var ist = utcNow.ToIst();
        if (ist.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var t = ist.TimeOfDay;
        return t >= MarketOpen && t <= MarketClose;
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        foreach (var (underlying, key, name) in Indices())
        {
            try { await PollIndexAsync(underlying, key, name, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (UnauthorizedTokenException) { throw; }   // no point trying the next index without a token
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{Index}: PCR poll failed.", name);
                if (DateTimeOffset.UtcNow - _lastWarning > TimeSpan.FromMinutes(10))
                {
                    _lastWarning = DateTimeOffset.UtcNow;
                    _logger.LogWarning(ex, "{Index}: PCR poll failed -- will retry.", name);
                }
            }
        }
    }

    private async Task PollIndexAsync(string underlying, string key, string name, CancellationToken ct)
    {
        var today = MarketTime.Today;
        var expiry = await _expiries.ResolveCurrentAsync(key, today, ct);

        var (spot, strikes) = await _rest.GetOptionChainAsync(key, expiry, ct);
        if (strikes.Count == 0 || spot <= 0)
        {
            _logger.LogDebug("{Index}: option chain for {Expiry} had no usable data (strikes={Count}, spot={Spot}).", name, expiry, strikes.Count, spot);
            return;
        }

        var result = PcrCalculator.Calculate(strikes, spot, _options.StrikeWindow);
        if (result is null) return;

        var label = PcrLabeler.Label(result.WindowPcr, _options);
        var now = DateTimeOffset.UtcNow;

        await _repo.SaveAsync(new PcrSnapshotEntity
        {
            Underlying = underlying,
            Time = now,
            Expiry = expiry,
            Spot = result.Spot,
            FullPcr = result.FullPcr,
            WindowPcr = result.WindowPcr,
            TotalCallOi = result.TotalCallOi,
            TotalPutOi = result.TotalPutOi,
            SupportStrike = result.SupportStrike,
            ResistanceStrike = result.ResistanceStrike,
            Label = label
        }, ct);

        var open = await DayOpenAsync(underlying, today, result.WindowPcr, ct);
        double? change = open is { } o && result.WindowPcr is { } cur ? cur - o : null;

        await PositionBroadcast.PublishPcrAsync(_hub, new PcrUpdateDto(
            underlying, now, expiry, result.Spot, result.WindowPcr, result.FullPcr, change, label,
            result.SupportStrike, result.ResistanceStrike), _logger);

        _logger.LogDebug("{Index}: PCR near-ATM {Window:0.00}, full {Full:0.00} ({Label}), expiry {Expiry}.",
            name, result.WindowPcr, result.FullPcr, label, expiry);
    }

    /// <summary>Near-ATM PCR of the day's first snapshot (from the DB after a restart, otherwise the one just calculated).</summary>
    private async Task<double?> DayOpenAsync(string underlying, DateOnly today, double? current, CancellationToken ct)
    {
        if (_dayOpen.TryGetValue(underlying, out var hit) && hit.Day == today && hit.Pcr is not null)
            return hit.Pcr;

        var first = await _repo.GetFirstOfDayAsync(underlying, today, ct);
        var pcr = first?.WindowPcr ?? current;
        _dayOpen[underlying] = (today, pcr);
        return pcr;
    }
}