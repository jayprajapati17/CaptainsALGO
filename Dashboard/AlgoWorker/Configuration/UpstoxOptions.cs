namespace AlgoWorker.Configuration;

/// <summary>
/// Upstox API configuration. AccessToken (fallback only) can still live in
/// config, but as of Task 6, the REAL source of truth is the AccessTokens
/// database table (one row per day, generated via the /auth/upstox/* endpoints
/// below) -- see UpstoxRestClient for the lookup-with-fallback logic.
/// </summary>
public sealed class UpstoxOptions
{
    public const string SectionName = "Upstox";

    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>Fallback only -- used if no token exists in the database for today yet.</summary>
    public string AccessToken { get; set; } = string.Empty;

    public string NiftyInstrumentKey { get; set; } = "NSE_INDEX|Nifty 50";
    public string BaseUrl { get; set; } = "https://api.upstox.com";
    public string WebSocketAuthUrl { get; set; } = "https://api.upstox.com/v3/feed/market-data-feed/authorize";
    public string HistoricalCandleUrlTemplate { get; set; } = string.Empty;
    public string IntradayCandleUrlTemplate { get; set; } = string.Empty;
    public string DailyCandleUrlTemplate { get; set; } = string.Empty;
    public string MarketStatusUrl { get; set; } = string.Empty;
    public string OptionContractUrlTemplate { get; set; } = string.Empty;

    // >>> NEW: for the 3-Minute MACD strategy -- same shape as HistoricalCandleUrlTemplate /
    // IntradayCandleUrlTemplate above, just a "3" interval instead of "15" baked in.
    public string HistoricalCandleUrlTemplate3Min { get; set; } = string.Empty;
    public string IntradayCandleUrlTemplate3Min { get; set; } = string.Empty;

    // >>> NEW (Task 6): OAuth endpoints, used by /auth/upstox/authorize and
    // /auth/upstox/callback (mapped in Program.cs) -- the Dashboard's
    // "Generate token" button opens /auth/upstox/authorize in a new tab;
    // everything else happens server-side in the Worker.
    /// <summary>MUST exactly match a Redirect URI registered against your app in the Upstox Developer Console.</summary>
    public string RedirectUri { get; set; } = "http://localhost:5050/auth/upstox/callback";
    public string LoginAuthorizeUrl { get; set; } = "https://api.upstox.com/v2/login/authorization/dialog";
    public string LoginTokenUrl { get; set; } = "https://api.upstox.com/v2/login/authorization/token";
}