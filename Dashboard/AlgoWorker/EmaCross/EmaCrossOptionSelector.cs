using AlgoWorker.Configuration;
using AlgoWorker.Models;
using AlgoWorker.Services;
using Microsoft.Extensions.Options;
using AlgoWorker.EmaCross.Core;

namespace AlgoWorker.EmaCross;

public sealed record OptionChoice(OptionInstrument Instrument, decimal Premium);

/// <summary>
/// Section 8.1 / 8.2 of the guide: the CE chain for a buy signal, the PE chain for a sell signal; the nearest expiry that
/// satisfies the variant's minimum days; among strikes whose premium is inside [PremiumMin, PremiumMax] the one closest to
/// PremiumTargetMid (open interest breaks a tie). No strike in range -> the next expiry -> otherwise the signal is skipped.
/// The range is never widened automatically. Expiry dates and strikes come from Upstox's contract list, never computed.
/// </summary>
public sealed class EmaCrossOptionSelector
{
    private readonly UpstoxRestClient _rest;
    private readonly ExpiryProvider _expiries;
    private readonly EmaCrossOptions _options;
    private readonly UpstoxOptions _upstox;
    private readonly ILogger<EmaCrossOptionSelector> _logger;

    public EmaCrossOptionSelector(UpstoxRestClient rest, ExpiryProvider expiries, IOptions<EmaCrossOptions> options,
        IOptions<UpstoxOptions> upstox, ILogger<EmaCrossOptionSelector> logger)
    {
        _rest = rest;
        _expiries = expiries;
        _options = options.Value;
        _upstox = upstox.Value;
        _logger = logger;
    }

    /// <returns>The chosen contract and its LTP, or null with <paramref name="skipReason"/> filled in.</returns>
    public async Task<(OptionChoice? Choice, string? SkipReason)> SelectAsync(Side side, DateOnly today, ExpiryRule rule, CancellationToken ct)
    {
        var key = _upstox.NiftyInstrumentKey;
        var type = side == Side.Long ? OptionType.Call : OptionType.Put;
        var min = (decimal)_options.PremiumMin;
        var max = (decimal)_options.PremiumMax;
        var target = (decimal)_options.PremiumTargetMid;

        var allExpiries = await _expiries.GetExpiriesAsync(key, ct);
        var candidates = allExpiries.Where(e => rule.Allows(today, e)).OrderBy(e => e).Take(2).ToList();   // nearest, then the next one
        if (candidates.Count == 0)
            return (null, $"No listed expiry with at least {rule.MinDays} {(rule.TradingDays ? "trading" : "calendar")} days left");

        var contracts = await _rest.GetOptionContractsAsync(key, ct);

        foreach (var expiry in candidates)
        {
            var chain = contracts.Where(c => c.Type == type && c.Expiry == expiry).ToList();
            if (chain.Count == 0) continue;

            var prices = await _rest.GetLastTradedPricesAsync(chain.Select(c => c.InstrumentKey).ToList(), ct);
            var inRange = chain
                .Where(c => prices.TryGetValue(c.InstrumentKey, out var p) && p >= min && p <= max)
                .Select(c => (Contract: c, Premium: prices[c.InstrumentKey]))
                .OrderBy(x => Math.Abs(x.Premium - target))
                .ToList();

            if (inRange.Count == 0)
            {
                _logger.LogInformation("EMA 9/21: no {Type} strike with premium {Min}-{Max} for expiry {Expiry}.", type, min, max, expiry);
                continue;
            }

            var best = inRange[0];
            // Tie (same distance from the target within 5 paise): prefer the strike with more open interest.
            var tied = inRange.Where(x => Math.Abs(Math.Abs(x.Premium - target) - Math.Abs(best.Premium - target)) <= 0.05m).ToList();
            if (tied.Count > 1)
                best = await PreferHigherOpenInterestAsync(tied, type, expiry, key, ct) ?? best;

            var c0 = best.Contract;
            var instrument = new OptionInstrument(c0.InstrumentKey, c0.TradingSymbol, c0.Strike, c0.Type, c0.Expiry);
            return (new OptionChoice(instrument, best.Premium), null);
        }

        return (null, $"No {(type == OptionType.Call ? "CE" : "PE")} strike with premium {min:0}-{max:0} in the next {candidates.Count} expiry(ies) -- range not widened");
    }

    private async Task<(OptionContract Contract, decimal Premium)?> PreferHigherOpenInterestAsync(
        List<(OptionContract Contract, decimal Premium)> tied, OptionType type, DateOnly expiry, string key, CancellationToken ct)
    {
        try
        {
            var (_, strikes) = await _rest.GetOptionChainAsync(key, expiry, ct);
            decimal Oi(OptionContract c) => strikes.FirstOrDefault(s => s.Strike == c.Strike) is { } s ? (type == OptionType.Call ? s.CallOi : s.PutOi) : 0;
            return tied.OrderByDescending(x => Oi(x.Contract)).First();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnauthorizedTokenException)
        {
            _logger.LogDebug(ex, "EMA 9/21: could not read open interest for the tie-break.");
            return null;
        }
    }
}
