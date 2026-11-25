using System.Text.Json;

namespace AlgoWorker.Services;

// Option chain: open interest per strike (used for the PCR).
public sealed partial class UpstoxRestClient
{
    private bool _optionChainShapeLogged;

    /// <summary>
    /// GET /v2/option/chain for one underlying and expiry. Each row of "data" is one strike with "call_options" and
    /// "put_options", each carrying "market_data.oi". Spot comes from "underlying_spot_price". Parsing is lenient: a
    /// row without OI counts as zero, a row without a strike is skipped. The first response of the run is logged
    /// (trimmed) so the shape is easy to check if the numbers ever look wrong.
    /// </summary>
    public async Task<(decimal Spot, List<OptionChainStrike> Strikes)> GetOptionChainAsync(
        string underlyingInstrumentKey, DateOnly expiry, CancellationToken ct)
    {
        var url = $"{_options.CurrentValue.BaseUrl}/v2/option/chain?instrument_key={Uri.EscapeDataString(underlyingInstrumentKey)}&expiry_date={expiry:yyyy-MM-dd}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyAuthHeaderAsync(request, ct);

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedTokenException("Upstox access token rejected (401) while fetching the option chain.");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!_optionChainShapeLogged)
        {
            _optionChainShapeLogged = true;
            _logger.LogInformation("Option chain sample ({Instrument}, {Expiry}): {Body}",
                underlyingInstrumentKey, expiry, body.Length > 700 ? body[..700] + "..." : body);
        }

        using var doc = JsonDocument.Parse(body);
        var strikes = new List<OptionChainStrike>();
        decimal spot = 0;

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return (0, strikes);

        foreach (var row in data.EnumerateArray())
        {
            if (spot == 0 && TryDecimal(row, "underlying_spot_price", out var sp)) spot = sp;
            if (!TryDecimal(row, "strike_price", out var strike)) continue;
            strikes.Add(new OptionChainStrike(strike, OpenInterest(row, "call_options"), OpenInterest(row, "put_options")));
        }
        return (spot, strikes);
    }

    private static decimal OpenInterest(JsonElement row, string side)
    {
        if (row.TryGetProperty(side, out var opt)
            && opt.ValueKind == JsonValueKind.Object
            && opt.TryGetProperty("market_data", out var md)
            && md.ValueKind == JsonValueKind.Object
            && TryDecimal(md, "oi", out var oi))
            return oi;
        return 0;
    }

    private static bool TryDecimal(JsonElement obj, string name, out decimal value)
    {
        value = 0;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetDecimal(out value);
        if (el.ValueKind == JsonValueKind.String)
            return decimal.TryParse(el.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        return false;
    }
}