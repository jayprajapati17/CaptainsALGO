using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AlgoWorker.Configuration;
using AlgoWorker.Models;
using AlgoWorker.Services.Persistence;

namespace AlgoWorker.Services;

// Prices and contracts: previous-day OHLC, option-contract lookups and last-traded prices.
public sealed partial class UpstoxRestClient
{
    public async Task<(decimal High, decimal Low, decimal Close, DateOnly TradingDay)?> GetPreviousTradingDayOhlcAsync(
        string instrumentKey, CancellationToken ct)
    {
        var today = AlgoData.Models.MarketTime.Today; // calendar boundary only, IClock not needed for a "which 10-day window to scan" hint
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

    /// <summary>
    /// >>> NEW (Bank Nifty): all option contracts of the underlying across every expiry (GET /v2/option/contract
    /// without expiry_date). The "expiry" field is parsed leniently (date string or epoch milliseconds).
    /// </summary>
    public async Task<List<OptionContract>> GetOptionContractsAsync(string underlyingInstrumentKey, CancellationToken ct)
    {
        var url = string.Format(_options.CurrentValue.OptionContractsAllUrlTemplate, Uri.EscapeDataString(underlyingInstrumentKey));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyAuthHeaderAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedTokenException("Upstox access token rejected (401) while fetching option contracts.");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var result = new List<OptionContract>();
        foreach (var row in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var typeText = row.TryGetProperty("instrument_type", out var t) ? t.GetString() : null;
            OptionType type;
            if (string.Equals(typeText, "CE", StringComparison.OrdinalIgnoreCase)) type = OptionType.Call;
            else if (string.Equals(typeText, "PE", StringComparison.OrdinalIgnoreCase)) type = OptionType.Put;
            else continue;

            if (!row.TryGetProperty("expiry", out var expiryEl) || !TryParseExpiry(expiryEl, out var expiry))
                continue;

            result.Add(new OptionContract(
                InstrumentKey: row.GetProperty("instrument_key").GetString()!,
                TradingSymbol: row.TryGetProperty("trading_symbol", out var ts) ? ts.GetString() ?? string.Empty : string.Empty,
                Strike: (int)row.GetProperty("strike_price").GetDecimal(),
                Type: type,
                Expiry: expiry));
        }

        _logger.LogInformation("Fetched {Count} option contracts for {Instrument} ({Expiries} expiries).",
            result.Count, underlyingInstrumentKey, result.Select(c => c.Expiry).Distinct().Count());
        return result;
    }

    private static bool TryParseExpiry(JsonElement el, out DateOnly expiry)
    {
        expiry = default;
        if (el.ValueKind == JsonValueKind.String && DateOnly.TryParse(el.GetString(), out expiry))
            return true;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var ms))
        {
            // Epoch milliseconds -> India date (UTC+5:30), independent of the server's own time zone.
            var ist = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(TimeSpan.FromMinutes(330));
            expiry = DateOnly.FromDateTime(ist.DateTime);
            return true;
        }
        return false;
    }

    /// <summary>
    /// >>> NEW (Bank Nifty): batch LTP (chunks of 400 keys). The response is keyed like "NSE_FO:BANKNIFTY..." so each entry is
    /// matched back to our instrument key through its "instrument_token" field ("NSE_FO|12345"); a key already equal to ours also works.
    /// </summary>
    public async Task<Dictionary<string, decimal>> GetLastTradedPricesAsync(IReadOnlyCollection<string> instrumentKeys, CancellationToken ct)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in instrumentKeys.Chunk(400))
        {
            var url = $"{_options.CurrentValue.BaseUrl}/v2/market-quote/ltp?instrument_key={Uri.EscapeDataString(string.Join(",", chunk))}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            await ApplyAuthHeaderAsync(request, ct);

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new UnauthorizedTokenException("Upstox access token rejected (401) while fetching LTP.");
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var prop in doc.RootElement.GetProperty("data").EnumerateObject())
            {
                if (!prop.Value.TryGetProperty("last_price", out var lp)) continue;
                var price = lp.GetDecimal();
                var token = prop.Value.TryGetProperty("instrument_token", out var it) ? it.GetString() : null;
                if (!string.IsNullOrEmpty(token)) result[token] = price;
                result[prop.Name] = price;
            }
        }
        return result;
    }
}
