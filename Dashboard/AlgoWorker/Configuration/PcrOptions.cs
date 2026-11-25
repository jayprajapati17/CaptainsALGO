namespace AlgoWorker.Configuration;

/// <summary>
/// Put/Call ratio (open interest) settings -- config section "Pcr", editable on the Dashboard's Settings page.
/// Pure information: no strategy reads the PCR. One set of values is used for both Nifty and Bank Nifty.
/// </summary>
public sealed class PcrOptions
{
    public const string SectionName = "Pcr";

    [Setting("PCR", "Turn the PCR (put/call ratio) tracking on/off.")]
    public bool Enabled { get; set; } = true;

    [Setting("PCR", "How often the option chain is read, in seconds (market hours only).")]
    public int PollSeconds { get; set; } = 90;

    [Setting("PCR", "Strikes on each side of ATM used for the 'near ATM' PCR (10 = 21 strikes in total).")]
    public int StrikeWindow { get; set; } = 10;

    [Setting("PCR", "Near-ATM PCR at or above this reads as 'Bullish-ish' (more put than call open interest).")]
    public double BullishAbove { get; set; } = 1.2;

    [Setting("PCR", "Near-ATM PCR at or below this reads as 'Bearish-ish'.")]
    public double BearishBelow { get; set; } = 0.8;

    [Setting("PCR", "Near-ATM PCR at or above this reads as 'Stretched high' (crowded; reversals are more likely).")]
    public double StretchedHigh { get; set; } = 1.5;

    [Setting("PCR", "Near-ATM PCR at or below this reads as 'Stretched low'.")]
    public double StretchedLow { get; set; } = 0.5;
}