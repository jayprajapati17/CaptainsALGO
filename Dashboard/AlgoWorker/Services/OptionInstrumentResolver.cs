using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AlgoWorker.Configuration;
using AlgoWorker.Models;

namespace AlgoWorker.Services;

// >>> NEW: pairs a resolved instrument with which leg it represents.
public sealed record ResolvedLeg(OptionLeg Leg, OptionInstrument Instrument);

public sealed class OptionInstrumentResolver
{
    private readonly UpstoxRestClient _restClient;
    private readonly ExpiryProvider _expiries;
    private readonly StrategyOptions _strategy;
    private readonly UpstoxOptions _upstox;
    private readonly ILogger<OptionInstrumentResolver> _logger;

    public OptionInstrumentResolver(
        UpstoxRestClient restClient,
        ExpiryProvider expiries,
        IOptions<StrategyOptions> strategy,
        IOptions<UpstoxOptions> upstox,
        ILogger<OptionInstrumentResolver> logger)
    {
        _restClient = restClient;
        _expiries = expiries;
        _strategy = strategy.Value;
        _upstox = upstox.Value;
        _logger = logger;
    }

    // >>> CHANGED: this used to return a single next-week ATM instrument.
    // Per the user's request, every signal now needs TWO legs:
    //   1) Current-week expiry, ITM strike (ItmStrikeDepth strikes deep)
    //   2) Next-week expiry, ATM strike
    // Both are resolved and returned; the caller (VirtualPositionTracker)
    // opens a separate tracked position for each one that resolves successfully.
    public Task<List<ResolvedLeg>> ResolveAllLegsAsync(TradeSignal signal, CancellationToken ct) =>
        ResolveAllLegsAsync(signal, _upstox.NiftyInstrumentKey, _strategy.StrikeStepPoints, _strategy.ItmStrikeDepth, ct);

    /// <summary>Same two legs for ANY index (underlying key, strike spacing and ITM depth come from that index's settings).</summary>
    public async Task<List<ResolvedLeg>> ResolveAllLegsAsync(TradeSignal signal, string underlyingKey, int strikeStep, int itmDepth, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(signal.ConfirmedAtCandleTime.Date);
        var optionType = signal.Direction == SignalDirection.GoldenCross ? OptionType.Call : OptionType.Put;
        var atmStrike = RoundToNearestStrike(signal.NiftySpotAtConfirmation, strikeStep);

        var results = new List<ResolvedLeg>();

        // --- Leg 1: current-expiry ITM ---
        var currentWeekExpiry = await _expiries.ResolveCurrentAsync(underlyingKey, today, ct);
        var itmStrike = ComputeItmStrike(atmStrike, optionType, itmDepth, strikeStep);

        _logger.LogInformation("Resolving CURRENT-EXPIRY ITM {Type} option on {Underlying}: strike {Strike} ({Depth} strikes ITM), expiry {Expiry}.",
            optionType, underlyingKey, itmStrike, itmDepth, currentWeekExpiry);

        var itmInstrument = await _restClient.FindNearestStrikeContractAsync(
            underlyingKey, currentWeekExpiry, itmStrike, optionType, ct);

        if (itmInstrument is not null)
            results.Add(new ResolvedLeg(OptionLeg.CurrentWeekItm, itmInstrument));
        else
            _logger.LogWarning("Could not resolve current-expiry ITM leg on {Underlying} (strike {Strike}, expiry {Expiry}). " +
                                "Possible causes: expiry shifted for a holiday, or option chain not listed.", underlyingKey, itmStrike, currentWeekExpiry);

        // --- Leg 2: next-expiry ATM ---
        var nextWeekExpiry = await _expiries.ResolveNextAsync(underlyingKey, today, ct);

        _logger.LogInformation("Resolving NEXT-EXPIRY ATM {Type} option on {Underlying}: strike {Strike}, expiry {Expiry}.",
            optionType, underlyingKey, atmStrike, nextWeekExpiry);

        var atmInstrument = await _restClient.FindNearestStrikeContractAsync(
            underlyingKey, nextWeekExpiry, atmStrike, optionType, ct);

        if (atmInstrument is not null)
            results.Add(new ResolvedLeg(OptionLeg.NextWeekAtm, atmInstrument));
        else
            _logger.LogWarning("Could not resolve next-expiry ATM leg on {Underlying} (strike {Strike}, expiry {Expiry}). " +
                                "Possible causes: expiry not listed this far out yet.", underlyingKey, atmStrike, nextWeekExpiry);

        return results;
    }

    private static int RoundToNearestStrike(decimal spot, int step)
        => (int)(Math.Round(spot / step, MidpointRounding.AwayFromZero) * step);

    // For the Opening Range Breakout (ORB) strategy -- simpler than ResolveAllLegsAsync:
    // just one current-expiry ATM contract, no ITM leg.
    public Task<OptionInstrument?> ResolveCurrentWeekAtmAsync(
        decimal spot, OptionType optionType, DateOnly today, CancellationToken ct) =>
        ResolveCurrentAtmAsync(_upstox.NiftyInstrumentKey, spot, optionType, _strategy.StrikeStepPoints, today, ct);

    /// <summary>
    /// Current-expiry ATM contract for ANY index (Nifty, Bank Nifty): the nearest listed expiry on/after today
    /// (from Upstox's own contract list) and the strike nearest to spot rounded to <paramref name="strikeStep"/>.
    /// </summary>
    public async Task<OptionInstrument?> ResolveCurrentAtmAsync(
        string underlyingKey, decimal spot, OptionType optionType, int strikeStep, DateOnly today, CancellationToken ct)
    {
        var expiry = await _expiries.ResolveCurrentAsync(underlyingKey, today, ct);
        var atmStrike = RoundToNearestStrike(spot, strikeStep);

        _logger.LogInformation("Resolving CURRENT-EXPIRY ATM {Type} option for breakout on {Underlying}: strike {Strike}, expiry {Expiry}.",
            optionType, underlyingKey, atmStrike, expiry);

        var instrument = await _restClient.FindNearestStrikeContractAsync(underlyingKey, expiry, atmStrike, optionType, ct);

        if (instrument is null)
            _logger.LogWarning("Could not resolve breakout ATM contract on {Underlying} (strike {Strike}, expiry {Expiry}).", underlyingKey, atmStrike, expiry);

        return instrument;
    }

    /// <summary>
    /// ITM direction depends on option type: a CALL is in-the-money BELOW spot,
    /// a PUT is in-the-money ABOVE spot. "N strikes ITM" moves N steps further
    /// into that in-the-money direction from the ATM strike.
    /// </summary>
    private static int ComputeItmStrike(int atmStrike, OptionType type, int depth, int step)
        => type == OptionType.Call
            ? atmStrike - (depth * step)
            : atmStrike + (depth * step);
}