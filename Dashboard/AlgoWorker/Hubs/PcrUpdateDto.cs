namespace AlgoWorker.Hubs;

/// <summary>
/// "PcrChanged" payload. <c>Underlying</c> is "NIFTY50"; <c>Change</c> is the near-ATM PCR minus the first
/// near-ATM PCR of the day (null until two readings exist). Property names go to the browser camel-cased.
/// </summary>
public sealed record PcrUpdateDto(
    string Underlying,
    DateTimeOffset Time,
    DateOnly Expiry,
    decimal Spot,
    double? WindowPcr,
    double? FullPcr,
    double? Change,
    string Label,
    decimal? SupportStrike,
    decimal? ResistanceStrike);