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

    [Setting("General", "Upstox app API key (client id).", Secret = true)]
    public string ApiKey { get; set; } = string.Empty;
    [Setting("General", "Upstox app API secret.", Secret = true)]
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>Fallback only -- used if no token exists in the database for today yet.</summary>
    [Setting("General", "Fallback token, used only if no token was generated for today via the Dashboard.", Secret = true)]
    public string AccessToken { get; set; } = string.Empty;

    [Setting("General", "Upstox instrument key of the Nifty 50 index.", RestartRequired = true)]
    public string NiftyInstrumentKey { get; set; } = "NSE_INDEX|Nifty 50";
    [Setting("General")]
    public string BaseUrl { get; set; } = "https://api.upstox.com";

    // >>> NEW (Bank Nifty pipeline): index to subscribe on the shared feed + the all-expiry contract list URL.
    [Setting("General")]
    public string BankNiftyInstrumentKey { get; set; } = "NSE_INDEX|Nifty Bank";
    /// <summary>All contracts of an underlying across every expiry ({0} = underlying instrument key). Different from OptionContractUrlTemplate, which is per expiry.</summary>
    [Setting("General")]
    public string OptionContractsAllUrlTemplate { get; set; } = "https://api.upstox.com/v2/option/contract?instrument_key={0}";
    [Setting("General")]
    public string WebSocketAuthUrl { get; set; } = "https://api.upstox.com/v3/feed/market-data-feed/authorize";
    [Setting("General")]
    public string HistoricalCandleUrlTemplate { get; set; } = string.Empty;
    [Setting("General")]
    public string IntradayCandleUrlTemplate { get; set; } = string.Empty;
    [Setting("General")]
    public string DailyCandleUrlTemplate { get; set; } = string.Empty;
    [Setting("General")]
    public string MarketStatusUrl { get; set; } = string.Empty;
    [Setting("General")]
    public string OptionContractUrlTemplate { get; set; } = string.Empty;

    // >>> NEW: for the 3-Minute MACD strategy -- same shape as HistoricalCandleUrlTemplate /
    // IntradayCandleUrlTemplate above, just a "3" interval instead of "15" baked in.
    [Setting("General")]
    public string HistoricalCandleUrlTemplate3Min { get; set; } = string.Empty;
    [Setting("General")]
    public string IntradayCandleUrlTemplate3Min { get; set; } = string.Empty;

    // >>> NEW (Task 6): OAuth endpoints, used by /auth/upstox/authorize and
    // /auth/upstox/callback (mapped in Program.cs) -- the Dashboard's
    // "Generate token" button opens /auth/upstox/authorize in a new tab;
    // everything else happens server-side in the Worker.
    /// <summary>MUST exactly match a Redirect URI registered against your app in the Upstox Developer Console.</summary>
    [Setting("General", "Must exactly match the Redirect URL registered in the Upstox Developer Console.")]
    public string RedirectUri { get; set; } = "http://localhost:5050/auth/upstox/callback";
    [Setting("General")]
    public string LoginAuthorizeUrl { get; set; } = "https://api.upstox.com/v2/login/authorization/dialog";
    [Setting("General")]
    public string LoginTokenUrl { get; set; } = "https://api.upstox.com/v2/login/authorization/token";
}