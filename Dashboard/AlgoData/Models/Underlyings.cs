namespace AlgoData.Models;

/// <summary>
/// The underlying keys stored in Positions.Underlying / SignalLog.Underlying and sent over SignalR.
/// One place for the strings, so a typo can never silently split Nifty and Bank Nifty rows.
/// </summary>
public static class Underlyings
{
    public const string Nifty50 = "NIFTY50";
    public const string BankNifty = "BANKNIFTY";

    /// <summary>Dashboard filter chip ("nifty" | "banknifty") to the stored key; null = no filtering ("all" or unknown).</summary>
    public static string? FromFilterKey(string? filterKey) => filterKey switch
    {
        "nifty" => Nifty50,
        "banknifty" => BankNifty,
        _ => null
    };
}

/// <summary>Shared "filter by underlying" step, so every query spells it the same way.</summary>
public static class UnderlyingQueryExtensions
{
    /// <summary>Null or empty underlying means "all instruments".</summary>
    public static IQueryable<PositionEntity> ForUnderlying(this IQueryable<PositionEntity> query, string? underlying) =>
        string.IsNullOrEmpty(underlying) ? query : query.Where(p => p.Underlying == underlying);

    public static IQueryable<SignalLogEntity> ForUnderlying(this IQueryable<SignalLogEntity> query, string? underlying) =>
        string.IsNullOrEmpty(underlying) ? query : query.Where(s => s.Underlying == underlying);
}
