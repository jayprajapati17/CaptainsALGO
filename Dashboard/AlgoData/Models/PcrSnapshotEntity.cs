namespace AlgoData.Models;

/// <summary>Maps 1:1 to the `PcrSnapshot` table: one row per index per poll (about every 90 seconds in market hours).</summary>
public sealed class PcrSnapshotEntity
{
    public long Id { get; set; }

    /// <summary>"NIFTY50" | "BANKNIFTY" (see <see cref="Underlyings"/>).</summary>
    public string Underlying { get; set; } = Underlyings.Nifty50;

    public DateTimeOffset Time { get; set; }
    public DateOnly Expiry { get; set; }
    public decimal Spot { get; set; }

    /// <summary>Put OI / Call OI over every strike. Null when there is no call OI.</summary>
    public double? FullPcr { get; set; }
    /// <summary>Same ratio over ATM +/- N strikes only. Null when there is no call OI there.</summary>
    public double? WindowPcr { get; set; }

    public decimal TotalCallOi { get; set; }
    public decimal TotalPutOi { get; set; }

    public decimal? SupportStrike { get; set; }
    public decimal? ResistanceStrike { get; set; }

    /// <summary>Bullish-ish / Neutral / Bearish-ish / Stretched high / Stretched low / No data, by the thresholds in force at the time.</summary>
    public string Label { get; set; } = string.Empty;
}