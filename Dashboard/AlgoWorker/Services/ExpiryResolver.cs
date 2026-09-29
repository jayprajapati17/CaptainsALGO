namespace AlgoWorker.Services;

/// <summary>
/// Nifty 50 weekly options expire every Tuesday. Per the user's explicit
/// requirement, this bot always trades NEXT week's expiry, never the
/// current week's -- see design doc discussion on option holding-period risk.
/// </summary>
public sealed class ExpiryResolver
{
    /// <summary>
    /// Returns the Tuesday that is strictly after the *upcoming* Tuesday
    /// (i.e. skips the current week's expiry, even if "today" already is a Tuesday).
    /// </summary>
    public DateOnly ResolveNextWeekExpiry(DateOnly today)
    {
        var thisWeeksExpiry = NextOrSameTuesday(today);
        return thisWeeksExpiry.AddDays(7);
    }

    // >>> NEW: exposes the CURRENT week's expiry, needed now that every
    // signal opens two legs -- a current-week ITM leg and a next-week ATM leg.
    public DateOnly ResolveCurrentWeekExpiry(DateOnly today) => NextOrSameTuesday(today);

    private static DateOnly NextOrSameTuesday(DateOnly date)
    {
        var daysUntilTuesday = ((int)DayOfWeek.Tuesday - (int)date.DayOfWeek + 7) % 7;
        return date.AddDays(daysUntilTuesday);
    }

    // NOTE: NSE occasionally shifts an expiry to Monday when Tuesday is a trading
    // holiday. This resolver does NOT currently know about holidays (per the
    // user's decision to use the live Exchange Status API instead of a static
    // holiday calendar -- see design doc Section 6, point 5). If precise
    // holiday-shifted expiries matter to you, cross-check the resolved date
    // against Upstox's Option Contract API response (it will simply have no
    // contracts for a shifted date) or Upstox's Market Holidays API before
    // trusting this date blindly for the last week of any holiday-heavy month.
}