using AlgoWorker.Configuration;

namespace AlgoWorker.Services;

/// <summary>One strike of an option chain: open interest on the call and on the put.</summary>
public sealed record OptionChainStrike(decimal Strike, decimal CallOi, decimal PutOi);

/// <summary>
/// PCR numbers for one option chain. "Full" uses every strike; "window" only the strikes around ATM, so far-away
/// strikes with old, stale open interest cannot distort the picture. Support / resistance are the strikes with the
/// most put / call open interest inside the window. PCR is null when there is no call open interest to divide by.
/// </summary>
public sealed record PcrResult(
    decimal Spot,
    decimal AtmStrike,
    double? FullPcr,
    double? WindowPcr,
    decimal TotalCallOi,
    decimal TotalPutOi,
    decimal WindowCallOi,
    decimal WindowPutOi,
    int StrikesInWindow,
    decimal? SupportStrike,
    decimal? ResistanceStrike);

public static class PcrCalculator
{
    /// <param name="chain">Every strike of one expiry (any order; duplicates of a strike are summed).</param>
    /// <param name="spot">Underlying spot at the time of the snapshot.</param>
    /// <param name="window">Strikes on EACH side of ATM that make up the near-ATM window (0 = ATM only).</param>
    public static PcrResult? Calculate(IEnumerable<OptionChainStrike> chain, decimal spot, int window)
    {
        var strikes = chain
            .GroupBy(s => s.Strike)
            .Select(g => new OptionChainStrike(g.Key, g.Sum(x => x.CallOi), g.Sum(x => x.PutOi)))
            .OrderBy(s => s.Strike)
            .ToList();
        if (strikes.Count == 0) return null;

        // ATM = the listed strike closest to spot (ties go to the lower strike).
        var atmIndex = 0;
        var best = decimal.MaxValue;
        for (var i = 0; i < strikes.Count; i++)
        {
            var d = Math.Abs(strikes[i].Strike - spot);
            if (d < best) { best = d; atmIndex = i; }
        }

        var w = Math.Max(0, window);
        var from = Math.Max(0, atmIndex - w);
        var to = Math.Min(strikes.Count - 1, atmIndex + w);
        var inWindow = strikes.Skip(from).Take(to - from + 1).ToList();

        var totalCall = strikes.Sum(s => s.CallOi);
        var totalPut = strikes.Sum(s => s.PutOi);
        var winCall = inWindow.Sum(s => s.CallOi);
        var winPut = inWindow.Sum(s => s.PutOi);

        var maxPut = inWindow.MaxBy(s => s.PutOi);
        var maxCall = inWindow.MaxBy(s => s.CallOi);

        return new PcrResult(
            Spot: spot,
            AtmStrike: strikes[atmIndex].Strike,
            FullPcr: Ratio(totalPut, totalCall),
            WindowPcr: Ratio(winPut, winCall),
            TotalCallOi: totalCall,
            TotalPutOi: totalPut,
            WindowCallOi: winCall,
            WindowPutOi: winPut,
            StrikesInWindow: inWindow.Count,
            SupportStrike: maxPut is { PutOi: > 0 } ? maxPut.Strike : null,
            ResistanceStrike: maxCall is { CallOi: > 0 } ? maxCall.Strike : null);
    }

    private static double? Ratio(decimal put, decimal call) => call <= 0 ? null : (double)(put / call);
}

/// <summary>Turns a PCR number into the short label the Live page shows. Thresholds come from <see cref="PcrOptions"/>.</summary>
public static class PcrLabeler
{
    public const string None = "No data";
    public const string StretchedHigh = "Stretched high";
    public const string Bullish = "Bullish-ish";
    public const string Neutral = "Neutral";
    public const string Bearish = "Bearish-ish";
    public const string StretchedLow = "Stretched low";

    public static string Label(double? pcr, PcrOptions o)
    {
        if (pcr is not { } v) return None;
        if (v >= o.StretchedHigh) return StretchedHigh;
        if (v >= o.BullishAbove) return Bullish;
        if (v <= o.StretchedLow) return StretchedLow;
        if (v <= o.BearishBelow) return Bearish;
        return Neutral;
    }
}