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
    private readonly ExpiryResolver _expiryResolver;
    private readonly StrategyOptions _strategy;
    private readonly UpstoxOptions _upstox;
    private readonly ILogger<OptionInstrumentResolver> _logger;

    public OptionInstrumentResolver(
        UpstoxRestClient restClient,
        ExpiryResolver expiryResolver,
        IOptions<StrategyOptions> strategy,
        IOptions<UpstoxOptions> upstox,
        ILogger<OptionInstrumentResolver> logger)
    {
        _restClient = restClient;
        _expiryResolver = expiryResolver;
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
    public async Task<List<ResolvedLeg>> ResolveAllLegsAsync(TradeSignal signal, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(signal.ConfirmedAtCandleTime.Date);
        var optionType = signal.Direction == SignalDirection.GoldenCross ? OptionType.Call : OptionType.Put;
        var atmStrike = RoundToNearestStrike(signal.NiftySpotAtConfirmation, _strategy.StrikeStepPoints);

        var results = new List<ResolvedLeg>();

        // --- Leg 1: current-week ITM ---
        var currentWeekExpiry = _expiryResolver.ResolveCurrentWeekExpiry(today);
        var itmStrike = ComputeItmStrike(atmStrike, optionType, _strategy.ItmStrikeDepth, _strategy.StrikeStepPoints);

        _logger.LogInformation("Resolving CURRENT-WEEK ITM {Type} option: strike {Strike} ({Depth} strikes ITM), expiry {Expiry}.",
            optionType, itmStrike, _strategy.ItmStrikeDepth, currentWeekExpiry);

        var itmInstrument = await _restClient.FindNearestStrikeContractAsync(
            _upstox.NiftyInstrumentKey, currentWeekExpiry, itmStrike, optionType, ct);

        if (itmInstrument is not null)
            results.Add(new ResolvedLeg(OptionLeg.CurrentWeekItm, itmInstrument));
        else
            _logger.LogWarning("Could not resolve current-week ITM leg (strike {Strike}, expiry {Expiry}). " +
                                "Possible causes: expiry shifted for a holiday, or option chain not listed.", itmStrike, currentWeekExpiry);

        // --- Leg 2: next-week ATM ---
        var nextWeekExpiry = _expiryResolver.ResolveNextWeekExpiry(today);

        _logger.LogInformation("Resolving NEXT-WEEK ATM {Type} option: strike {Strike}, expiry {Expiry}.",
            optionType, atmStrike, nextWeekExpiry);

        var atmInstrument = await _restClient.FindNearestStrikeContractAsync(
            _upstox.NiftyInstrumentKey, nextWeekExpiry, atmStrike, optionType, ct);

        if (atmInstrument is not null)
            results.Add(new ResolvedLeg(OptionLeg.NextWeekAtm, atmInstrument));
        else
            _logger.LogWarning("Could not resolve next-week ATM leg (strike {Strike}, expiry {Expiry}). " +
                                "Possible causes: expiry not listed this far out yet.", atmStrike, nextWeekExpiry);

        return results;
    }

    private static int RoundToNearestStrike(decimal spot, int step)
        => (int)(Math.Round(spot / step, MidpointRounding.AwayFromZero) * step);

    // >>> NEW: for the Previous-Day High/Low Breakout strategy -- simpler than
    // ResolveAllLegsAsync, just one current-week ATM contract, no ITM leg.
    public async Task<OptionInstrument?> ResolveCurrentWeekAtmAsync(
        decimal spot, OptionType optionType, DateOnly today, CancellationToken ct)
    {
        var expiry = _expiryResolver.ResolveCurrentWeekExpiry(today);
        var atmStrike = RoundToNearestStrike(spot, _strategy.StrikeStepPoints);

        _logger.LogInformation("Resolving CURRENT-WEEK ATM {Type} option for breakout: strike {Strike}, expiry {Expiry}.",
            optionType, atmStrike, expiry);

        var instrument = await _restClient.FindNearestStrikeContractAsync(
            _upstox.NiftyInstrumentKey, expiry, atmStrike, optionType, ct);

        if (instrument is null)
            _logger.LogWarning("Could not resolve breakout ATM contract (strike {Strike}, expiry {Expiry}).", atmStrike, expiry);

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