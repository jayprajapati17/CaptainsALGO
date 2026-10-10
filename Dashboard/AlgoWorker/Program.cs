using AlgoData.Data;
using AlgoWorker;
using AlgoWorker.Configuration;
using AlgoWorker.Hubs;
using AlgoWorker.Services;
using AlgoWorker.Services.Decoding;
using AlgoWorker.Services.Indicators;
using AlgoWorker.Services.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Telegram.Bot;

// >>> CHANGED (Task 2): was `Host.CreateApplicationBuilder(args)`. Now
// `WebApplication.CreateBuilder(args)` so this process can ALSO host Kestrel
// (SignalR hub in this task; Command API + OAuth callback come in Tasks 5/6).
// The existing `Worker : BackgroundService` is completely unchanged and still
// registered the same way (`AddHostedService<Worker>()`) -- it keeps running
// exactly as before, just inside a host that can also serve HTTP.
var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile(
    $"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddUserSecrets<Program>(optional: true);

// >>> NEW (DB-backed settings): Upstox / Telegram / Strategy values saved from the Dashboard's
// Settings page live in the AppSettings table. Added LAST so a value in the DB overrides the same
// key in appsettings.json / env vars. NiftyBot:ConnectionString itself (and Instruments, Serilog)
// stay in appsettings.json -- the DB address can't be read from the DB.
DbConfigurationSource? dbConfigSource = null;
var bootstrapConnectionString = builder.Configuration["NiftyBot:ConnectionString"];
if (!string.IsNullOrWhiteSpace(bootstrapConnectionString))
{
    dbConfigSource = new DbConfigurationSource(bootstrapConnectionString);
    ((Microsoft.Extensions.Configuration.IConfigurationBuilder)builder.Configuration).Add(dbConfigSource);
}

// >>> NEW: make the Worker listen on the address everything else expects.
// Without this a WebApplication with no launchSettings binds to http://localhost:5000, so the
// Dashboard (WorkerBaseUrl = http://localhost:5050) and the Upstox OAuth RedirectUri
// (http://localhost:5050/auth/upstox/callback) could never reach it. Only applied when
// nothing else configures it -- an explicit "Urls" / "Kestrel:Endpoints" setting or
// ASPNETCORE_URLS still wins (e.g. when deployed elsewhere).
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])
    && !builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
{
    builder.WebHost.UseUrls("http://localhost:5050");
}

// >>> NEW: default CORS origin for the Dashboard's browser-side SignalR connection, if
// NiftyBot:DashboardOrigin isn't set in appsettings.json (it wasn't -- the hub would have
// been blocked by the browser even once the port was right).
if (string.IsNullOrWhiteSpace(builder.Configuration["NiftyBot:DashboardOrigin"]))
{
    builder.Configuration["NiftyBot:DashboardOrigin"] = "http://localhost:5080";
}

// >>> NEW: print this LOUDLY at startup -- a wrong/missing DashboardOrigin is a silent
// CORS failure otherwise (the browser console shows the error, but nothing here did).
//Console.WriteLine($"[NiftyBot] Dashboard CORS origin resolved to: {builder.Configuration["NiftyBot:DashboardOrigin"]}");

// ---- Serilog ----
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    //.WriteTo.Console()
    .WriteTo.File(
        path: "logs/nifty-bot-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Services.AddSerilog();

// ---- Options binding ----
builder.Services.Configure<UpstoxOptions>(builder.Configuration.GetSection(UpstoxOptions.SectionName));
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.Configure<StrategyOptions>(builder.Configuration.GetSection(StrategyOptions.SectionName));

// >>> NEW (multi-instrument, Phase 1): root-level "Instruments" JSON array binds
// directly to List<InstrumentDefinition> -- no wrapper object needed. Not yet
// consumed by the live trading pipeline (that's Phase 2+); this just makes the
// config and the two services below available to build on.
builder.Services.Configure<System.Collections.Generic.List<InstrumentDefinition>>(builder.Configuration.GetSection("Instruments"));
builder.Services.AddSingleton<InstrumentRegistry>();
builder.Services.AddSingleton<CapitalBasedStrikeSelector>();

// ---- HTTP client for Upstox REST calls ----
builder.Services.AddHttpClient<UpstoxRestClient>();

// ---- Telegram bot client ----
builder.Services.AddSingleton<ITelegramBotClient>(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TelegramOptions>>().Value;
    if (string.IsNullOrWhiteSpace(opts.BotToken))
        Log.Warning("Telegram BotToken is empty -- alerts will fail to send until configured.");
    return new TelegramBotClient(opts.BotToken);
});

// ---- NEW (Task 1, wired here): shared database ----
builder.Services.AddNiftyBotDatabase(builder.Configuration["NiftyBot:ConnectionString"]!);

// ---- Core pipeline services (all singletons -- one shared pipeline per process) ----
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IMarketDataDecoder, ProtobufMarketDataDecoder>();
builder.Services.AddSingleton<UpstoxWebSocketClient>();
// >>> CHANGED: was AddSingleton<CandleAggregatorService>() with no factory, which
// resolves via the class's own default (15 minutes), ignoring config. Now explicitly
// uses the configured EmaCandleMinutes (StrategyOptions), default still 15.
builder.Services.AddSingleton<CandleAggregatorService>(sp =>
    new CandleAggregatorService(TimeSpan.FromMinutes(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StrategyOptions>>().Value.EmaCandleMinutes)));
builder.Services.AddSingleton<IndicatorEngine>();
builder.Services.AddSingleton<SignalEngine>();
builder.Services.AddSingleton<ExpiryResolver>();
builder.Services.AddSingleton<OptionInstrumentResolver>();
builder.Services.AddSingleton<VirtualPositionTracker>();
builder.Services.AddSingleton<MarketHoursService>();
builder.Services.AddSingleton<TelegramAlertService>();
builder.Services.AddSingleton<HistoricalSeederService>();
builder.Services.AddSingleton<FiveMinCandleAggregatorService>();
builder.Services.AddSingleton<BreakoutSignalEngine>();
builder.Services.AddSingleton<BreakoutPositionTracker>();
builder.Services.AddSingleton<ReversalSignalEngine>();
builder.Services.AddSingleton<ReversalPositionTracker>();
builder.Services.AddSingleton<FiveMinHistoricalSeederService>();

// >>> NEW: 3-Minute MACD(12,26,9) + Dynamic Step-Trailing SL strategy --
// third independent pipeline, own 3-min candle stream.
builder.Services.AddSingleton<ThreeMinCandleAggregatorService>();
builder.Services.AddSingleton<MacdEngine>();
builder.Services.AddSingleton<MacdSignalEngine>();
builder.Services.AddSingleton<MacdPositionTracker>();
builder.Services.AddSingleton<MacdHistoricalSeederService>();

builder.Services.AddSingleton<CprAnalysisService>();

// ---- NEW (Task 3): persistence repositories ----
builder.Services.AddSingleton<IPositionRepository, PositionRepository>();
builder.Services.AddSingleton<ISignalLogRepository, SignalLogRepository>();
builder.Services.AddSingleton<ICprRepository, CprRepository>();

// ---- NEW (Task 6): token repository + a plain HttpClientFactory for the OAuth token exchange call ----
builder.Services.AddSingleton<ITokenRepository, TokenRepository>();
builder.Services.AddHttpClient();

// ---- NEW (Task 4): SignalR + CORS (Dashboard's browser JS connects to the
// hub from a different origin/port, so CORS must explicitly allow it). ----
builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddPolicy("DashboardCors", policy =>
    {
        var dashboardOrigin = builder.Configuration["NiftyBot:DashboardOrigin"];
        if (string.IsNullOrWhiteSpace(dashboardOrigin))
        {
            Log.Warning("NiftyBot:DashboardOrigin is not configured -- the Dashboard's browser JS will not be able to connect to the SignalR hub until this is set (e.g. \"http://localhost:5080\").");
            return;
        }

        // Browsers never send a trailing slash in the Origin header -- WithOrigins needs
        // an EXACT match, so "http://localhost:5080/" would silently reject everything.
        dashboardOrigin = dashboardOrigin.TrimEnd('/');

        policy.WithOrigins(dashboardOrigin)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // required for SignalR
    });
});

builder.Services.AddHostedService<Worker>();

var app = builder.Build();

// >>> CHANGED: EnsureNiftyBotSchema() call removed -- per your request, the
// schema is applied MANUALLY (run Data/schema-sqlserver.sql yourself in
// SSMS/Azure Data Studio), not automatically by the app at startup.

// >>> NEW (Task 4): CORS must be applied before endpoint routing/mapping.
app.UseCors("DashboardCors");

// >>> NEW (Task 4): the actual hub endpoint the Dashboard's browser JS connects to.
app.MapHub<PositionHub>("/hubs/positions");

// =====================================================================
// >>> NEW (Task 5): Command API -- manual "close position" from the Dashboard.
// Called server-to-server (Dashboard's C# backend -> here), not directly
// from the browser, so no CORS handling is needed for these.
// =====================================================================

app.MapPost("/api/positions/{id:int}/close", async (
    int id, VirtualPositionTracker vpt, BreakoutPositionTracker bpt, MacdPositionTracker mpt, ReversalPositionTracker rpt, CancellationToken ct) =>
{
    if (await vpt.ForceCloseAsync(id, ct))
        return Results.Ok(new { closed = true, strategy = "Ema" });

    if (await bpt.ForceCloseAsync(id, ct))
        return Results.Ok(new { closed = true, strategy = "Breakout" });

    if (await mpt.ForceCloseAsync(id, ct))
        return Results.Ok(new { closed = true, strategy = "Macd" });

    if (await rpt.ForceCloseAsync(id, ct))
        return Results.Ok(new { closed = true, strategy = "Reversal" });

    return Results.NotFound(new { closed = false, message = $"No OPEN position found with Id {id}." });
});

app.MapGet("/api/positions/open", (VirtualPositionTracker vpt, BreakoutPositionTracker bpt, MacdPositionTracker mpt, ReversalPositionTracker rpt) =>
{
    var all = vpt.GetOpenPositionsSnapshot()
        .Concat(bpt.GetOpenPositionsSnapshot())
        .Concat(mpt.GetOpenPositionsSnapshot())
        .Concat(rpt.GetOpenPositionsSnapshot());
    return Results.Ok(all);
});

// =====================================================================
// >>> NEW (Task 6): Upstox OAuth -- the Dashboard's "Generate token" button
// opens /auth/upstox/authorize in a new browser tab; everything else
// (login/OTP at Upstox, redirect back, token exchange, DB save) happens
// here in the Worker, which owns the Upstox ApiKey/ApiSecret. This is a
// full-page browser navigation, not an AJAX call, so CORS doesn't apply.
// =====================================================================

app.MapGet("/auth/upstox/authorize", (Microsoft.Extensions.Options.IOptions<UpstoxOptions> upstoxOptions) =>
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
    Microsoft.Extensions.Options.IOptions<UpstoxOptions> upstoxOptions,
    ITokenRepository tokenRepository,
    IHubContext<PositionHub> hub, // >>> NEW (Task 9): so the Dashboard's token widget flips instantly, no polling
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
        return Results.Content($"<html><body><h2>Token exchange failed. Check the Worker's logs.</h2></body></html>", htmlContentType);
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

    // >>> NEW (Task 9): per the design doc ("Worker turant SignalR se Dashboard ko batata hai
    // token generated") -- the Dashboard's token widget listens for this and updates immediately,
    // with no refresh and no polling needed. Non-fatal if the hub push fails -- the token is
    // already saved; the widget's own 60s poll (token.js) will pick it up regardless.
    try
    {
        await hub.Clients.All.SendAsync("TokenGenerated", new { generatedAt = DateTimeOffset.Now }, ct);
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

// =====================================================================
// >>> NEW (DB-backed settings): the Dashboard's Settings page saves rows into the AppSettings
// table, then calls this endpoint (server-to-server). We re-read the table and push the new
// values into the live options objects. Anything the Worker only reads at startup is flagged
// RequiresRestart in the table and shown as such on the Settings page.
// =====================================================================
static void RebindOptions(IServiceProvider sp, IConfiguration config)
{
    // Services hold on to IOptions<T>.Value (one shared instance), so updating that instance IN PLACE
    // makes the new values visible everywhere immediately. IOptionsMonitor<T> consumers (the REST
    // client) refresh through the configuration reload token.
    config.GetSection(UpstoxOptions.SectionName).Bind(sp.GetRequiredService<IOptions<UpstoxOptions>>().Value);
    config.GetSection(TelegramOptions.SectionName).Bind(sp.GetRequiredService<IOptions<TelegramOptions>>().Value);
    config.GetSection(StrategyOptions.SectionName).Bind(sp.GetRequiredService<IOptions<StrategyOptions>>().Value);
}

app.MapPost("/api/settings/reload", (IServiceProvider sp, IConfiguration config, ILogger<Program> logger) =>
{
    if (dbConfigSource?.Provider is null)
        return Results.Problem("DB-backed settings are not available (NiftyBot:ConnectionString missing).");

    dbConfigSource.Provider.Reload();
    RebindOptions(sp, config);
    logger.LogInformation("Settings reloaded from the AppSettings table (triggered by the Dashboard).");
    return Results.Ok(new { reloaded = true });
});

// Create the AppSettings table if needed and add a row for every setting that doesn't have one yet
// (first run: copies the current appsettings.json values in; later runs: only brand-new settings).
try
{
    using var settingsScope = app.Services.CreateScope();
    var scopeSp = settingsScope.ServiceProvider;
    await SettingsSeeder.SeedAsync(
        scopeSp.GetRequiredService<IDbContextFactory<AlgoData.Data.NiftyBotDbContext>>(),
        scopeSp.GetRequiredService<IOptions<UpstoxOptions>>().Value,
        scopeSp.GetRequiredService<IOptions<TelegramOptions>>().Value,
        scopeSp.GetRequiredService<IOptions<StrategyOptions>>().Value,
        scopeSp.GetRequiredService<ILogger<Program>>(),
        CancellationToken.None);
    await SchemaMigrator.ApplyAsync(
        scopeSp.GetRequiredService<IDbContextFactory<AlgoData.Data.NiftyBotDbContext>>(),
        scopeSp.GetRequiredService<ILogger<Program>>(),
        CancellationToken.None);

    dbConfigSource?.Provider?.Reload();
    RebindOptions(app.Services, app.Configuration);
}
catch (Exception ex)
{
    Log.Warning(ex, "Settings: could not create/seed the AppSettings table -- continuing with appsettings.json values.");
}

// >>> NEW (Task 3): trackers keep open positions in memory only, so any row still
// marked Open in the DB at process start is an orphan from a previous run
// (crash/redeploy/reboot). Close them out now so the Dashboard's Live page never
// shows phantom positions that nothing is tracking anymore.
using (var scope = app.Services.CreateScope())
{
    var positionRepository = scope.ServiceProvider.GetRequiredService<AlgoWorker.Services.Persistence.IPositionRepository>();
    await positionRepository.CloseOrphanedOpenPositionsAsync("Worker restarted -- position no longer tracked", CancellationToken.None);
}

// >>> CHANGED (Task 2): was `await host.RunAsync();` on a plain Host. Same
// idea, `WebApplication` exposes RunAsync too.
await app.RunAsync();