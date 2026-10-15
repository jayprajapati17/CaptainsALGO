using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AlgoWorker.Configuration;
using AlgoWorker.Models;
using AlgoWorker.Services.Persistence;

namespace AlgoWorker.Services;

// Candle history / intraday fetches (15-, 5- and 3-minute) for the indicator seeders and the live aggregators.
public sealed partial class UpstoxRestClient
{
    public async Task<List<Candle>> GetHistoricalCandlesAsync(
        string instrumentKey, DateOnly from, DateOnly to, int intervalMinutes, CancellationToken ct)
    {
        var url = string.Format(
            _options.CurrentValue.HistoricalCandleUrlTemplate,
            Uri.EscapeDataString(instrumentKey),
            to.ToString("yyyy-MM-dd"),
            from.ToString("yyyy-MM-dd"),
            intervalMinutes);

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


    // >>> NEW: 5-min candle fetch (Reversal strategy + ORB seeding). Reuses the 3-min URL templates
    // from config with the interval swapped, so no new config keys are needed.
    private static string To5Min(string template3Min) => template3Min.Replace("/minutes/3", "/minutes/5");

    public async Task<List<Candle>> GetHistoricalCandles5MinAsync(string instrumentKey, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var url = string.Format(
            To5Min(_options.CurrentValue.HistoricalCandleUrlTemplate3Min),
            Uri.EscapeDataString(instrumentKey),
            to.ToString("yyyy-MM-dd"),
            from.ToString("yyyy-MM-dd"));
        return await FetchCandleListAsync(url, "5-min historical", ct);
    }

    public async Task<List<Candle>> GetIntraday5MinCandlesAsync(string instrumentKey, CancellationToken ct)
    {
        var url = string.Format(
            To5Min(_options.CurrentValue.IntradayCandleUrlTemplate3Min),
            Uri.EscapeDataString(instrumentKey));
        return await FetchCandleListAsync(url, "5-min intraday", ct);
    }

    private async Task<List<Candle>> FetchCandleListAsync(string url, string what, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyAuthHeaderAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedTokenException($"Upstox access token rejected (401) while fetching {what} candles.");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var candles = new List<Candle>();
        foreach (var row in doc.RootElement.GetProperty("data").GetProperty("candles").EnumerateArray())
        {
            var arr = row.EnumerateArray().ToArray();
            candles.Add(new Candle
            {
                OpenTime = DateTimeOffset.Parse(arr[0].GetString()!),
                Open = arr[1].GetDecimal(),
                High = arr[2].GetDecimal(),
                Low = arr[3].GetDecimal(),
                Close = arr[4].GetDecimal(),
                Volume = arr[5].GetInt64(),
                IsClosed = true
            });
        }

        candles.Reverse();
        _logger.LogInformation("Fetched {Count} {What} candles.", candles.Count, what);
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
        string instrumentKey, DateOnly from, DateOnly to, int intervalMinutes, CancellationToken ct)
    {
        var url = string.Format(
            _options.CurrentValue.HistoricalCandleUrlTemplate3Min,
            Uri.EscapeDataString(instrumentKey),
            to.ToString("yyyy-MM-dd"),
            from.ToString("yyyy-MM-dd"),
            intervalMinutes);

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
        _logger.LogInformation("Fetched {Count} historical {Interval}-min candles for {Instrument} ({From} to {To})",
            candles.Count, intervalMinutes, instrumentKey, from, to);
        return candles;
    }

    public async Task<List<Candle>> GetIntraday3MinCandlesAsync(string instrumentKey, int intervalMinutes, CancellationToken ct)
    {
        var url = string.Format(
            _options.CurrentValue.IntradayCandleUrlTemplate3Min,
            Uri.EscapeDataString(instrumentKey),
            intervalMinutes);

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
        _logger.LogInformation("Fetched {Count} intraday (today's) {Interval}-min candles for {Instrument}", candles.Count, intervalMinutes, instrumentKey);
        return candles;
    }
}
