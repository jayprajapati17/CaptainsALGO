using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AlgoWorker.Configuration;
using AlgoWorker.Models;
using AlgoWorker.Services.Persistence;

namespace AlgoWorker.Services;

/// <summary>One option contract from the Upstox contract list (all expiries) -- used by the Bank Nifty strike selector.</summary>
public sealed record OptionContract(string InstrumentKey, string TradingSymbol, int Strike, OptionType Type, DateOnly Expiry);

public enum ExchangeMarketState { PreOpen, NormalOpen, NormalClose, Closed, Unknown }

/// <summary>Upstox REST client. Core (auth, token check, market status, feed URL) lives here; candles and quotes are in the partial files next to it.</summary>
public sealed partial class UpstoxRestClient
{
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<UpstoxOptions> _options;
    private readonly ITokenRepository _tokenRepository; // >>> NEW (Task 6)
    private readonly ILogger<UpstoxRestClient> _logger;

    public UpstoxRestClient(
        HttpClient http,
        IOptionsMonitor<UpstoxOptions> options,
        ITokenRepository tokenRepository,
        ILogger<UpstoxRestClient> logger)
    {
        _http = http;
        _options = options;
        _tokenRepository = tokenRepository;
        _logger = logger;
    }

    // >>> CHANGED (Task 6): was a synchronous method reading only
    // _options.CurrentValue.AccessToken. Now async -- looks up TODAY's token
    // from the database first (the real source of truth since the dashboard's
    // "Generate token" button writes there), falling back to appsettings.json
    // only if the database has nothing for today yet.
    private async Task ApplyAuthHeaderAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var todaysToken = (await _tokenRepository.GetTodaysTokenEntityAsync(ct))?.Token;
        var token = !string.IsNullOrWhiteSpace(todaysToken) ? todaysToken : _options.CurrentValue.AccessToken;

        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("No Upstox access token available (neither in the database for today, nor in appsettings.json) -- request will likely be rejected as 401.");
        }
        else if (string.IsNullOrWhiteSpace(todaysToken))
        {
            _logger.LogDebug("Using appsettings.json fallback token -- no database token found for today yet.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Checks whether the currently-resolved AccessToken (DB-first, config-fallback)
    /// is accepted by Upstox. Cheapest way to do this is to call the
    /// market-status endpoint, which requires auth.
    /// </summary>
    public async Task<bool> IsAccessTokenValidAsync(CancellationToken ct)
    {
        try
        {
            var (state, _) = await GetMarketStatusAsync(ct);
            return state != ExchangeMarketState.Unknown;
        }
        catch (UnauthorizedTokenException)
        {
            return false;
        }
    }

    public async Task<(ExchangeMarketState State, string Raw)> GetMarketStatusAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.CurrentValue.MarketStatusUrl);
        await ApplyAuthHeaderAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedTokenException("Upstox access token rejected (401) while checking market status.");

        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var statusText = doc.RootElement
            .GetProperty("data")
            .GetProperty("status")
            .GetString() ?? "UNKNOWN";

        var state = statusText.ToUpperInvariant() switch
        {
            "PRE_OPEN_START" or "PRE_OPEN_END" => ExchangeMarketState.PreOpen,
            "NORMAL_OPEN" => ExchangeMarketState.NormalOpen,
            "NORMAL_CLOSE" or "CLOSING_START" or "CLOSING_END" => ExchangeMarketState.NormalClose,
            "CLOSED" => ExchangeMarketState.Closed,
            _ => ExchangeMarketState.Unknown
        };

        return (state, body);
    }

    public async Task<string> GetAuthorizedWebSocketUrlAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.CurrentValue.WebSocketAuthUrl);
        await ApplyAuthHeaderAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedTokenException("Upstox access token rejected (401) while authorizing WebSocket feed.");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").GetProperty("authorized_redirect_uri").GetString()
            ?? throw new InvalidOperationException("authorized_redirect_uri missing from response.");
    }
}

/// <summary>Thrown when Upstox rejects the resolved AccessToken (401). Signals "please refresh the token".</summary>
public sealed class UnauthorizedTokenException : Exception
{
    public UnauthorizedTokenException(string message) : base(message) { }
}