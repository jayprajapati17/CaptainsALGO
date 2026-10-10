namespace AlgoWorker.Hubs;

/// <summary>
/// >>> NEW (Task 4): shape broadcast over PositionHub's "PositionChanged"
/// event. The Dashboard's browser JS receives this as plain JSON -- no need
/// for it to live in NiftyBot.Shared, since only Worker-side C# code
/// constructs it and only browser JS (not Dashboard's server-side C#)
/// consumes it directly.
///
/// PositionHub also broadcasts two other, simpler, anonymous-object events
/// (no dedicated DTO type, just `new { ... }` at the call site):
///   - "SpotChanged" { price, time } -- Worker.cs, throttled ~1/sec, for the Live page's header.
///   - "TokenGenerated" { generatedAt } -- Program.cs's OAuth callback (Task 9), for the navbar's token widget.
/// </summary>
public sealed record PositionUpdateDto(
    int PositionId,
    string Strategy,       // "Ema" | "Breakout" | "Macd"
    string Direction,
    string? Leg,            // "CurrentWeekItm" | "NextWeekAtm" | null
    string TradingSymbol,   // "NIFTY50 24350CE"
    int Strike,
    string OptionType,      // "CE" | "PE"
    DateOnly Expiry,
    decimal EntryPremium,
    DateTimeOffset EntryTime,
    decimal CurrentPremium,
    decimal PnlRupees,
    double PnlPercent,
    string Status,           // "Open" | "Closed"
    string? ExitReason,
    decimal StopLossPremium = 0m,  // current (trailing) stop-loss premium; 0 = n/a
    decimal StopLossSpot = 0m,     // Reversal only: current stop-loss as a Nifty spot level; 0 = n/a
    bool StopLossTrailing = false); // Reversal only: true once the SL has moved to cost (after T1)