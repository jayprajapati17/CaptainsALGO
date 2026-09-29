namespace AlgoData.Models;

/// <summary>
/// Maps 1:1 to the `AccessTokens` table in schema-sqlserver.sql. One row per
/// calendar day (UNIQUE constraint on TokenDate) -- the dashboard's
/// "Generate token" button checks whether today's row already exists to
/// decide button-vs-label state.
/// </summary>
public sealed class AccessTokenEntity
{
    public int Id { get; set; }
    public DateOnly TokenDate { get; set; }
    public string Token { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; }
}