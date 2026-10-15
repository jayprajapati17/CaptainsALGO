using AlgoData.Models;
using AlgoWorker.BankNifty;
using AlgoWorker.Configuration;
using AlgoWorker.Hubs;
using AlgoWorker.Services;
using AlgoWorker.Services.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AlgoWorker.Startup;

/// <summary>
/// Every HTTP endpoint the Worker serves, grouped by purpose. The Dashboard's C# backend calls the command / settings /
/// telegram / token endpoints server-to-server (no CORS needed); the browser only talks to the SignalR hub and, for
/// "Generate token", navigates to /auth/upstox/authorize.
/// </summary>
public static class EndpointMapping
{
    public static WebApplication MapWorkerEndpoints(this WebApplication app, DbConfigurationSource? dbConfigSource)
    {
        app.MapHub<PositionHub>("/hubs/positions");
        app.MapPositionEndpoints();
        app.MapUpstoxAuthEndpoints();
        app.MapTelegramEndpoints();
        app.MapSettingsEndpoints(dbConfigSource);
        app.MapHealthEndpoint();
        return app;
    }

    // ---- Command API: manual close + open-position snapshot ----
    private static void MapPositionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/positions/{id:int}/close", async (
            int id, VirtualPositionTracker vpt, BreakoutPositionTracker bpt, MacdPositionTracker mpt,
            ReversalPositionTracker rpt, BankNiftyPositionTracker bnt, BankNiftyEmaPipeline bne, CancellationToken ct) =>
        {
            if (await vpt.ForceCloseAsync(id, ct))
                return Results.Ok(new { closed = true, strategy = "Ema" });

            if (await bpt.ForceCloseAsync(id, ct))
                return Results.Ok(new { closed = true, strategy = "Breakout" });

            if (await mpt.ForceCloseAsync(id, ct))
                return Results.Ok(new { closed = true, strategy = "Macd" });

            if (await rpt.ForceCloseAsync(id, ct))
                return Results.Ok(new { closed = true, strategy = "Reversal" });

            if (await bnt.ForceCloseAsync(id, ct))
                return Results.Ok(new { closed = true, strategy = "Breakout", underlying = Underlyings.BankNifty });

            if (await bne.Tracker.ForceCloseAsync(id, ct))
                return Results.Ok(new { closed = true, strategy = "Ema", underlying = Underlyings.BankNifty });

            return Results.NotFound(new { closed = false, message = $"No OPEN position found with Id {id}." });
        });

        app.MapGet("/api/positions/open", (VirtualPositionTracker vpt, BreakoutPositionTracker bpt, MacdPositionTracker mpt,
            ReversalPositionTracker rpt, BankNiftyPositionTracker bnt, BankNiftyEmaPipeline bne) =>
        {
            var all = vpt.GetOpenPositionsSnapshot()
                .Concat(bpt.GetOpenPositionsSnapshot())
                .Concat(mpt.GetOpenPositionsSnapshot())
                .Concat(rpt.GetOpenPositionsSnapshot())
                .Concat(bnt.GetOpenPositionsSnapshot())
                .Concat(bne.Tracker.GetOpenPositionsSnapshot());
            return Results.Ok(all);
        });
    }

    // ---- Upstox OAuth: "Generate token" opens /authorize in a new tab; login/OTP happens at Upstox, which redirects to /callback ----
    private static void MapUpstoxAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/upstox/authorize", (IOptions<UpstoxOptions> upstoxOptions) =>
        {
            var opts = upstoxOptions.Value;
            var url =
                $"{opts.LoginAuthorizeUrl}?response_type=code" +
                $"&client_id={Uri.EscapeDataString(opts.ApiKey)}" +
                $"&redirect_uri={Uri.EscapeDataString(opts.RedirectUri)}";
            return Results.Redirect(url);
        });

        app.MapGet("/auth/upstox/callback", async (
            string? code,
            string? error,
            IHttpClientFactory httpClientFactory,
            IOptions<UpstoxOptions> upstoxOptions,
            ITokenRepository tokenRepository,
            IHubContext<PositionHub> hub,   // so the Dashboard's token widget flips instantly, no polling
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            const string htmlContentType = "text/html";

            if (error is not null || string.IsNullOrWhiteSpace(code))
            {
                logger.LogWarning("Upstox OAuth callback returned an error or no code (error={Error}).", error);
                return Results.Content("<html><body><h2>Login failed. Please close this tab and try again.</h2></body></html>", htmlContentType);
            }

            var opts = upstoxOptions.Value;
            var http = httpClientFactory.CreateClient();

            var tokenRequestBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = opts.ApiKey,
                ["client_secret"] = opts.ApiSecret,
                ["redirect_uri"] = opts.RedirectUri,
                ["grant_type"] = "authorization_code"
            });

            using var tokenResponse = await http.PostAsync(opts.LoginTokenUrl, tokenRequestBody, ct);
            var tokenBody = await tokenResponse.Content.ReadAsStringAsync(ct);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                logger.LogError("Upstox token exchange failed ({Status}): {Body}", (int)tokenResponse.StatusCode, tokenBody);
                return Results.Content("<html><body><h2>Token exchange failed. Check the Worker's logs.</h2></body></html>", htmlContentType);
            }

            using var doc = System.Text.Json.JsonDocument.Parse(tokenBody);
            var accessToken = doc.RootElement.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                logger.LogError("Upstox token exchange succeeded but no access_token was present in the response: {Body}", tokenBody);
                return Results.Content("<html><body><h2>No access token received. Check the Worker's logs.</h2></body></html>", htmlContentType);
            }

            await tokenRepository.SaveTodaysTokenAsync(accessToken, ct);
            logger.LogInformation("Upstox access token generated and saved via the dashboard OAuth flow.");

            // Non-fatal if the hub push fails -- the token is already saved; the widget's own 60s poll picks it up anyway.
            try
            {
                await hub.Clients.All.SendAsync("TokenGenerated", new { generatedAt = DateTimeOffset.UtcNow }, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to broadcast TokenGenerated -- the Dashboard will still pick it up on its next status poll.");
            }

            return Results.Content(
                "<html><body><h2>Token generated successfully. You can close this tab and return to the dashboard.</h2></body></html>",
                htmlContentType);
        });

        app.MapGet("/api/token/status", async (ITokenRepository tokenRepository, CancellationToken ct) =>
        {
            var entity = await tokenRepository.GetTodaysTokenEntityAsync(ct);
            return Results.Ok(new
            {
                generated = entity is not null,
                generatedAt = entity?.GeneratedAt
            });
        });
    }

    // ---- Settings page "Send test message": sends one message to the configured chat and reports the real result ----
    private static void MapTelegramEndpoints(this WebApplication app)
    {
        app.MapPost("/api/telegram/test", async (TelegramAlertService telegram, CancellationToken ct) =>
        {
            var (ok, error) = await telegram.SendTestAsync(ct);
            if (ok) return Results.Ok(new { sent = true });
            return Results.Json(new { sent = false, error }, statusCode: 502);
        });
    }

    // ---- The Dashboard saved rows into AppSettings: re-read the table and push the values into the live options ----
    private static void MapSettingsEndpoints(this WebApplication app, DbConfigurationSource? dbConfigSource)
    {
        app.MapPost("/api/settings/reload", (IServiceProvider sp, IConfiguration config, ILogger<Program> logger) =>
        {
            if (dbConfigSource?.Provider is null)
                return Results.Problem("DB-backed settings are not available (NiftyBot:ConnectionString missing).");

            dbConfigSource.Provider.Reload();
            StartupTasks.RebindOptions(sp, config);
            logger.LogInformation("Settings reloaded from the AppSettings table (triggered by the Dashboard).");
            return Results.Ok(new { reloaded = true });
        });
    }

    // ---- /health: is the feed alive? 200 = ok, 503 = something is wrong while the market should be open ----
    private static void MapHealthEndpoint(this WebApplication app)
    {
        app.MapGet("/health", async (
            UpstoxWebSocketClient feed, MarketHoursService marketHours, ITokenRepository tokens,
            IOptions<UpstoxOptions> upstox, IEnumerable<IMarketFeedConsumer> consumers, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var marketShouldBeOpen = marketHours.IsWithinConfiguredWindow(now);

            var token = await tokens.GetTodaysTokenEntityAsync(ct);

            IndexHealth Index(string name, string key)
            {
                var at = feed.LastTickAt(key);
                return new IndexHealth(name, key, at, at is null ? null : Math.Round((now - at.Value).TotalSeconds, 1));
            }

            var indices = new List<IndexHealth> { Index("Nifty 50", upstox.Value.NiftyInstrumentKey) };
            foreach (var key in consumers.SelectMany(c => c.FeedInstrumentKeys).Distinct())
                indices.Add(Index(key == upstox.Value.BankNiftyInstrumentKey ? "Bank Nifty" : key, key));

            // "Stale" = no tick for 2 minutes while the market should be open (an index ticks several times a second).
            var stale = indices.Any(i => i.AgeSeconds is null || i.AgeSeconds > 120);

            var problems = new List<string>();
            if (marketShouldBeOpen && token is null) problems.Add("no Upstox token for today");
            if (marketShouldBeOpen && !feed.IsConnected) problems.Add("WebSocket not connected");
            if (marketShouldBeOpen && feed.IsConnected && stale) problems.Add("no recent index tick");

            var body = new
            {
                status = problems.Count == 0 ? "ok" : "degraded",
                problems,
                time = now,
                marketShouldBeOpen,
                token = new { generated = token is not null, generatedAt = token?.GeneratedAt },
                feed = new { connected = feed.IsConnected, subscribed = feed.SubscribedCount, indices }
            };
            return problems.Count == 0 ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
    }

    private sealed record IndexHealth(string Name, string InstrumentKey, DateTimeOffset? LastTickAt, double? AgeSeconds);
}