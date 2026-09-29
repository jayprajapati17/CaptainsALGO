using AlgoData.Models;

namespace AlgoWorker.Services.Persistence;

/// <summary>
/// >>> NEW (Task 6): reads/writes the AccessTokens table -- one row per day.
/// This is now the REAL source of truth for the Upstox access token (not
/// appsettings.json, which remains only as a fallback -- see UpstoxRestClient).
/// </summary>
public interface ITokenRepository
{
    /// <summary>Returns today's token row, or null if none has been generated yet today.</summary>
    Task<AccessTokenEntity?> GetTodaysTokenEntityAsync(CancellationToken ct);

    /// <summary>Upserts today's token (safe to call again if regenerated same day).</summary>
    Task SaveTodaysTokenAsync(string token, CancellationToken ct);
}