using AlgoWorker.Models;

namespace AlgoWorker.Services;

/// <summary>Result of a capital-budget strike search -- the chosen contract, its current premium, and the actual rupees it will deploy for 1 lot.</summary>
public sealed record CapitalFitResult(OptionInstrument Instrument, decimal Premium, decimal CapitalRupees);

/// <summary>
/// For instruments configured with PositionSizingMode.CapitalBudget (Bank Nifty,
/// stocks): instead of always trading the ATM strike regardless of cost, walks
/// OUT of the money (cheaper direction) from ATM until it finds the strike
/// whose premium x LotSize fits within the configured budget, always 1 lot.
///
/// Calls get cheaper as strike moves UP (away from spot); Puts get cheaper as
/// strike moves DOWN. ATM itself is tried first -- if it already fits, that's
/// the pick (this only moves OTM when ATM is too expensive, it never moves ITM).
/// </summary>
public sealed class CapitalBasedStrikeSelector
{
    private const int MaxStrikesToTry = 15; // generous enough for any instrument's typical premium decay curve

    private readonly UpstoxRestClient _restClient;
    private readonly ILogger<CapitalBasedStrikeSelector> _logger;

    public CapitalBasedStrikeSelector(UpstoxRestClient restClient, ILogger<CapitalBasedStrikeSelector> logger)
    {
        _restClient = restClient;
        _logger = logger;
    }

    public async Task<CapitalFitResult?> SelectAsync(
        string underlyingInstrumentKey, DateOnly expiry, int atmStrike, OptionType optionType,
        int strikeStep, int lotSize, decimal budgetRupees, CancellationToken ct)
    {
        var otmDirection = optionType == OptionType.Call ? 1 : -1; // Call: higher strike = cheaper. Put: lower strike = cheaper.

        OptionInstrument? fallbackInstrument = null;
        decimal fallbackPremium = 0;

        for (var i = 0; i <= MaxStrikesToTry; i++)
        {
            var strike = atmStrike + i * strikeStep * otmDirection;

            var instrument = await _restClient.FindNearestStrikeContractAsync(underlyingInstrumentKey, expiry, strike, optionType, ct);
            if (instrument is null)
                continue; // this far out, the strike may simply not be listed -- keep walking

            decimal premium;
            try
            {
                premium = await _restClient.GetLastTradedPriceAsync(instrument.InstrumentKey, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not fetch LTP for candidate strike {Strike} while sizing to budget -- trying the next one.", strike);
                continue;
            }

            var cost = premium * lotSize;
            if (cost <= budgetRupees)
                return new CapitalFitResult(instrument, premium, cost);

            // Keep the FIRST (closest-to-ATM) priced contract as a fallback, in case
            // nothing ever fits the budget (e.g. budget is simply too small for 1 lot
            // of this instrument) -- better to alert on the nearest strike than to
            // silently skip the signal entirely.
            fallbackInstrument ??= instrument;
            if (fallbackInstrument == instrument) fallbackPremium = premium;
        }

        if (fallbackInstrument is not null)
        {
            _logger.LogWarning(
                "No strike within {MaxTries} steps of ATM fit the {Budget:N0} budget for {Key} -- using the nearest-ATM strike anyway (₹{Cost:N0} for 1 lot, over budget).",
                MaxStrikesToTry, budgetRupees, underlyingInstrumentKey, fallbackPremium * lotSize);
            return new CapitalFitResult(fallbackInstrument, fallbackPremium, fallbackPremium * lotSize);
        }

        _logger.LogWarning("Could not resolve ANY strike (within {MaxTries} steps of ATM) for {Key}, expiry {Expiry}.", MaxStrikesToTry, underlyingInstrumentKey, expiry);
        return null;
    }
}