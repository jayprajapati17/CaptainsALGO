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
    string? ExitReason);

/// <summary>Compact CPR reading for the Live page's header strip.</summary>
public sealed class CprStrip
{
    public DateOnly ForTradingDay { get; init; }
    public bool IsForToday { get; init; }
    public decimal Pivot { get; init; }
    public decimal Tc { get; init; }
    public decimal Bc { get; init; }
    public double WidthPercent { get; init; }
    public string Reading { get; init; } = string.Empty;
    public string BiasNote { get; init; } = string.Empty;
}

public sealed class LiveIndexViewModel
{
    public List<OpenPositionDto> Positions { get; set; } = new();
    public string PositionsHubUrl { get; set; } = string.Empty;

    /// <summary>Set when the Worker Service couldn't be reached for the initial load.</summary>
    public string? WorkerError { get; set; }

    public CprStrip? Cpr { get; set; }
}