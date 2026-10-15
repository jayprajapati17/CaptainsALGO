using System.Globalization;

namespace AlgoWorker.Services;

/// <summary>Parses the "HH:mm" time settings (and "HH:mm:ss", which a browser time input may submit) the same way everywhere.</summary>
public static class TimeOfDay
{
    private static readonly string[] Formats = { "HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss" };

    public static TimeOnly Parse(string hhmm) =>
        TimeOnly.ParseExact(hhmm.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None);

    public static bool TryParse(string? hhmm, out TimeOnly time) =>
        TimeOnly.TryParseExact((hhmm ?? string.Empty).Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>The exchange (IST) wall-clock time of a timestamp, whatever offset it carries (UTC now, +05:30 for older rows and Upstox candles).</summary>
    public static TimeOnly Of(DateTimeOffset when) => TimeOnly.FromDateTime(AlgoData.Models.MarketTime.ToIst(when).DateTime);
}
