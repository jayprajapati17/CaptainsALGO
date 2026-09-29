using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AlgoWorker.Configuration;
using AlgoWorker.Models;
using AlgoWorker.Services.Persistence;

namespace AlgoWorker.Services;

public enum ExchangeMarketState { PreOpen, NormalOpen, NormalClose, Closed, Unknown }

public sealed class UpstoxRestClient
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

    public async Task<List<Candle>> GetHistoricalCandlesAsync(
        string instrumentKey, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var url = string.Format(
            _options.CurrentValue.HistoricalCandleUrlTemplate,
            Uri.EscapeDataString(instrumentKey),
            to.ToString("yyyy-MM-dd"),
            from.ToString("yyyy-MM-dd"));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyAuthHeaderAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedTokenException("Upstox access token rejected (401) while fetching historical candles.");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var candles = new List<Candle>();
        foreach (var row in doc.RootElement.GetProperty("data").GetProperty("candles").EnumerateArray())
        {
            var arr = row.EnumerateArray().ToArray();
            var ts = DateTimeOffset.Parse(arr[0].GetString()!);
            candles.Add(new Candle
            {
                OpenTime = ts,
                Open = arr[1].GetDecimal(),
                High = arr[2].GetDecimal(),
                Low = arr[3].GetDecimal(),
                Close = arr[4].GetDecimal(),
                Volume = arr[5].GetInt64(),
                IsClosed = true
            });
        }

        candles.Reverse();
        _logger.LogInformation("Fetched {Count} historical 15-min candles for {Instrument} ({From} to {To})",
            candles.Count, instrumentKey, from, to);
        return candles;
    }

    public async Task<List<Candle>> GetIntradayCandlesAsync(string instrumentKey, int intervalMinutes, CancellationToken ct)
    {
        var url = string.Format(
            _options.CurrentValue.IntradayCandleUrlTemplate,
            Uri.EscapeDataString(instrumentKey),
            intervalMinutes);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyAuthHeaderAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedTokenException("Upstox access token rejected (401) while fetching intraday candles.");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var candles = new List<Candle>();
        foreach (var row in doc.RootElement.GetProperty("data").GetProperty("candles").EnumerateArray())
        {
            var arr = row.EnumerateArray().ToArray();
            var ts = DateTimeOffset.Parse(arr[0].GetString()!);
            candles.Add(new Candle
            {
                OpenTime = ts,
                Open = arr[1].GetDecimal(),
                High = arr[2].GetDecimal(),
                Low = arr[3].GetDecimal(),
                Close = arr[4].GetDecimal(),
                Volume = arr[5].GetInt64(),
                IsClosed = true
            });
        }

        candles.Reverse();
        _logger.LogInformation("Fetched {Count} intraday (today's) {Interval}-min candles for {Instrument}", candles.Count, intervalMinutes, instrumentKey);
        return candles;
    }

    // >>> NEW: dedicated 3-min candle methods for the MACD strategy's seeder.
    // NOTE: kept separate rather than reusing GetHistoricalCandlesAsync /
    // GetIntradayCandlesAsync above, because HistoricalCandleUrlTemplate has
    // "minutes/15" hardcoded (not a {1} placeholder), and IntradayCandleUrlTemplate
    // has no {1} placeholder either (its intervalMinutes parameter is silently
    // ignored by string.Format) -- both effectively fixed at 15-min regardless of
    // what's passed in today. Rather than touch that shared code path, these use
    // their own template config keys with "3" baked in.

    public async Task<List<Candle>> GetHistoricalCandles3MinAsync(
        string instrumentKey, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var url = string.Format(
            _options.CurrentValue.HistoricalCandleUrlTemplate3Min,
            Uri.EscapeDataString(instrumentKey),
            to.ToString("yyyy-MM-dd"),
            from.ToString("yyyy-MM-dd"));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyAuthHeaderAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedTokenException("Upstox access token rejected (401) while fetching 3-min historical candles.");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var candles = new List<Candle>();
        foreach (var row in doc.RootElement.GetProperty("data").GetProperty("candles").EnumerateArray())
        {
            var arr = row.EnumerateArray().ToArray();
            var ts = DateTimeOffset.Parse(arr[0].GetString()!);
            candles.Add(new Candle
            {
                OpenTime = ts,
                Open = arr[1].GetDecimal(),
                High = arr[2].GetDecimal(),
                Low = arr[3].GetDecimal(),
                Close = arr[4].GetDecimal(),
                Volume = arr[5].GetInt64(),
                IsClosed = true
            });
        }

        candles.Reverse();
        _logger.LogInformation("Fetched {Count} historical 3-min candles for {Instrument} ({From} to {To})",
            candles.Count, instrumentKey, from, to);
        return candles;
    }

    public async Task<List<Candle>> GetIntraday3MinCandlesAsync(string instrumentKey, CancellationToken ct)
    {
        var url = string.Format(
            _options.CurrentValue.IntradayCandleUrlTemplate3Min,
            Uri.EscapeDataString(instrumentKey));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyAuthHeaderAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedTokenException("Upstox access token rejected (401) while fetching 3-min intraday candles.");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var candles = new List<Candle>();
        foreach (var row in doc.RootElement.GetProperty("data").GetProperty("candles").EnumerateArray())
        {
            var arr = row.EnumerateArray().ToArray();
            var ts = DateTimeOffset.Parse(arr[0].GetString()!);
            candles.Add(new Candle
            {
                OpenTime = ts,
                Open = arr[1].GetDecimal(),
                High = arr[2].GetDecimal(),
                Low = arr[3].GetDecimal(),
                Close = arr[4].GetDecimal(),
                Volume = arr[5].GetInt64(),
                IsClosed = true
            });
        }

        candles.Reverse();
        _logger.LogInformation("Fetched {Count} intraday (today's) 3-min candles for {Instrument}", candles.Count, instrumentKey);
        return candles;
    }

    public async Task<(decimal High, decimal Low, decimal Close, DateOnly TradingDay)?> GetPreviousTradingDayOhlcAsync(
        string instrumentKey, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today); // calendar boundary only, IClock not needed for a "which 10-day window to scan" hint
        var yesterday = today.AddDays(-1);
        var from = yesterday.AddDays(-10);

        var url = string.Format(
            _options.CurrentValue.DailyCandleUrlTemplate,
            Uri.EscapeDataString(instrumentKey),
            yesterday.ToString("yyyy-MM-dd"),
            from.ToString("yyyy-MM-dd"));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyAuthHeaderAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedTokenException("Upstox access token rejected (401) while fetching daily candles.");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        DateOnly? latestDay = null;
        decimal latestHigh = 0, latestLow = 0, latestClose = 0;

        foreach (var row in doc.RootElement.GetProperty("data").GetProperty("candles").EnumerateArray())
        {
            var arr = row.EnumerateArray().ToArray();
            var ts = DateTimeOffset.Parse(arr[0].GetString()!);
            latestDay = DateOnly.FromDateTime(ts.Date);
            latestHigh = arr[2].GetDecimal();
            latestLow = arr[3].GetDecimal();
            latestClose = arr[4].GetDecimal();
            break;
        }

        if (latestDay is null)
        {
            _logger.LogWarning("No daily candles returned for {Instrument} between {From} and {To} -- cannot determine previous day OHLC.",
                instrumentKey, from, yesterday);
            return null;
        }

        _logger.LogInformation("Previous trading day for {Instrument}: {Day}, High={High}, Low={Low}, Close={Close}",
            instrumentKey, latestDay, latestHigh, latestLow, latestClose);
        return (latestHigh, latestLow, latestClose, latestDay.Value);
    }

    public async Task<OptionInstrument?> FindNearestStrikeContractAsync(
        string underlyingInstrumentKey, DateOnly expiry, int targetStrike, OptionType type, CancellationToken ct)
    {
        var url = string.Format(
            _options.CurrentValue.OptionContractUrlTemplate,
            Uri.EscapeDataString(underlyingInstrumentKey),
            expiry.ToString("yyyy-MM-dd"));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyAuthHeaderAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedTokenException("Upstox access token rejected (401) while fetching option contracts.");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        OptionInstrument? best = null;
        var bestDistance = int.MaxValue;
        var wantedType = type == OptionType.Call ? "CE" : "PE";

        foreach (var row in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var instrumentType = row.GetProperty("instrument_type").GetString();
            if (!string.Equals(instrumentType, wantedType, StringComparison.OrdinalIgnoreCase))
                continue;

            var strike = (int)row.GetProperty("strike_price").GetDecimal();
            var distance = Math.Abs(strike - targetStrike);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = new OptionInstrument(
                    InstrumentKey: row.GetProperty("instrument_key").GetString()!,
                    TradingSymbol: row.GetProperty("trading_symbol").GetString() ?? string.Empty,
                    Strike: strike,
                    Type: type,
                    Expiry: expiry);
            }
        }

        if (best is null)
            _logger.LogWarning("No {Type} contract found near strike {Strike} for expiry {Expiry}", wantedType, targetStrike, expiry);

        return best;
    }

    public async Task<decimal> GetLastTradedPriceAsync(string instrumentKey, CancellationToken ct)
    {
        var url = $"{_options.CurrentValue.BaseUrl}/v2/market-quote/ltp?instrument_key={Uri.EscapeDataString(instrumentKey)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyAuthHeaderAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedTokenException("Upstox access token rejected (401) while fetching LTP.");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        foreach (var prop in doc.RootElement.GetProperty("data").EnumerateObject())
        {
            return prop.Value.GetProperty("last_price").GetDecimal();
        }

        throw new InvalidOperationException($"No LTP data returned for {instrumentKey}");
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