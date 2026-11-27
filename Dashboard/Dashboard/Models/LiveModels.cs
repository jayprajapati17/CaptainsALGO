using System.Text.Json.Serialization;

namespace Dashboard.Models;

/// <summary>
/// Mirror of the Worker's PositionUpdateDto (NiftyEmaAlertBot/Hubs/PositionUpdateDto.cs) --
/// same shape as GET /api/positions/open returns and as the "PositionChanged" SignalR event carries.
/// Duplicated here (not shared) on purpose: the Worker's version lives in its own project and
/// only the JSON shape is the contract between the two apps.
/// </summary>
public sealed record OpenPositionDto(
    int PositionId,
    string Strategy,
    string Direction,
    string? Leg,
    string TradingSymbol,
    int Strike,
    string OptionType,
    DateOnly Expiry,
    decimal EntryPremium,
    DateTimeOffset EntryTime,
    decimal CurrentPremium,
    decimal PnlRupees,
    double PnlPercent,
    string Status,
    string? ExitReason,
    decimal StopLossPremium = 0m,
    decimal StopLossSpot = 0m,
    bool StopLossTrailing = false,
    string Underlying = AlgoData.Models.Underlyings.Nifty50);   // "NIFTY50"

/// <summary>Compact CPR reading for the Live page's header strip.</summary>
public sealed class CprStrip
{
    public DateOnly ForTradingDay { get; init; }
    public bool IsForToday { get; init; }
    /// <summary>The close the CPR was computed from. For today's row that is the previous session's close, which the Live page measures the day's change against.</summary>
    public decimal SourceClose { get; init; }
    public decimal Pivot { get; init; }
    public decimal Tc { get; init; }
    public decimal Bc { get; init; }
    public decimal R1 { get; init; }
    public decimal S1 { get; init; }
    public decimal R2 { get; init; }
    public decimal S2 { get; init; }
    public double WidthPercent { get; init; }
    public string Reading { get; init; } = string.Empty;
    public string BiasNote { get; init; } = string.Empty;

    /// <summary>Reading without the explanation: "Narrow CPR -- Trending day likely..." -> "Narrow CPR".</summary>
    public string ShortReading => Reading.Split(" --")[0];
}

/// <summary>Latest put/call ratio of one index (from the PcrSnapshot table), shown on the index card and updated live over SignalR.</summary>
public sealed class PcrStrip
{
    public DateTimeOffset Time { get; init; }
    public bool IsToday { get; init; }
    public DateOnly Expiry { get; init; }
    public double? WindowPcr { get; init; }
    public double? FullPcr { get; init; }
    /// <summary>Near-ATM PCR minus the first near-ATM PCR of that day; null when there is only one reading.</summary>
    public double? Change { get; init; }
    public string Label { get; init; } = string.Empty;
    public decimal? Support { get; init; }
    public decimal? Resistance { get; init; }

    /// <summary>CSS modifier for the label chip (same mapping as live.js).</summary>
    public string LabelClass => Label switch
    {
        "Bullish-ish" => "pcr-bull",
        "Bearish-ish" => "pcr-bear",
        "Stretched high" or "Stretched low" => "pcr-stretch",
        _ => "pcr-neutral"
    };
}

/// <summary>One index's CPR for the Live page: today's range, and the range expected tomorrow (null until the after-close job has run).</summary>
public sealed class CprPair
{
    public string Name { get; init; } = string.Empty;
    /// <summary>Element-id / data key used by live.js to place the live-spot marker ("nifty").</summary>
    public string Key { get; init; } = string.Empty;
    public CprStrip? Today { get; init; }
    public CprStrip? Tomorrow { get; init; }
    /// <summary>Latest put/call ratio of this index; null until the Worker has stored one.</summary>
    public PcrStrip? Pcr { get; init; }
}

public sealed class LiveIndexViewModel
{
    public List<OpenPositionDto> Positions { get; set; } = new();
    public string PositionsHubUrl { get; set; } = string.Empty;

    /// <summary>Set when the Worker Service couldn't be reached for the initial load.</summary>
    public string? WorkerError { get; set; }

    public CprPair NiftyCpr { get; set; } = new() { Name = "Nifty 50", Key = "nifty" };

    /// <summary>Target (points above entry) shown as a marker on each position's gauge -- read from the AppSettings table.</summary>
    public double TargetPoints { get; set; } = 30;
    public double InitialRiskPoints { get; set; } = 15;
}