using AlgoData.Data;
using AlgoWorker.BankNifty;
using AlgoWorker.Configuration;
using AlgoWorker.Services;
using AlgoWorker.Services.Decoding;
using AlgoWorker.Services.Indicators;
using AlgoWorker.Services.Persistence;
using Microsoft.Extensions.Options;
using Serilog;
using Telegram.Bot;

namespace AlgoWorker.Startup;

/// <summary>Dependency-injection registrations, grouped by what they belong to.</summary>
public static class ServiceRegistration
{
    /// <summary>The four options sections (Upstox, Telegram, Strategy, BankNifty) and the multi-instrument config.</summary>
    public static IServiceCollection AddWorkerOptions(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<UpstoxOptions>(config.GetSection(UpstoxOptions.SectionName));
        services.Configure<TelegramOptions>(config.GetSection(TelegramOptions.SectionName));
        services.Configure<StrategyOptions>(config.GetSection(StrategyOptions.SectionName));
        services.Configure<BankNiftyOptions>(config.GetSection(BankNiftyOptions.SectionName));

        // Root-level "Instruments" array: config-only so far, not consumed by the live pipeline yet.
        services.Configure<List<InstrumentDefinition>>(config.GetSection("Instruments"));
        services.AddSingleton<InstrumentRegistry>();
        services.AddSingleton<CapitalBasedStrikeSelector>();
        return services;
    }

    /// <summary>Everything every pipeline shares: Upstox REST + feed, Telegram, database, repositories, clock, token storage.</summary>
    public static IServiceCollection AddSharedInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpClient<UpstoxRestClient>();

        services.AddSingleton<ITelegramBotClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<TelegramOptions>>().Value;
            if (string.IsNullOrWhiteSpace(opts.BotToken))
                Log.Warning("Telegram BotToken is empty -- alerts will fail to send until configured.");
            return new TelegramBotClient(opts.BotToken);
        });

        services.AddNiftyBotDatabase(config["NiftyBot:ConnectionString"]!);

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IMarketDataDecoder, ProtobufMarketDataDecoder>();
        services.AddSingleton<UpstoxWebSocketClient>();
        services.AddSingleton<MarketHoursService>();
        services.AddSingleton<TelegramAlertService>();

        services.AddSingleton<IPositionRepository, PositionRepository>();
        services.AddSingleton<ISignalLogRepository, SignalLogRepository>();
        services.AddSingleton<ICprRepository, CprRepository>();
        services.AddSingleton<ITokenRepository, TokenRepository>();
        services.AddHttpClient();   // plain HttpClientFactory for the OAuth token exchange
        return services;
    }

    /// <summary>The Nifty pipelines: EMA 21/50 + ADX, Opening Range Breakout, Upside Reversal, 3-min MACD, CPR -- and the Worker that drives them.</summary>
    public static IServiceCollection AddNiftyStrategies(this IServiceCollection services)
    {
        // EMA strategy (candle size comes from the configured EmaCandleMinutes, default 15).
        services.AddSingleton<CandleAggregatorService>(sp =>
            new CandleAggregatorService(TimeSpan.FromMinutes(sp.GetRequiredService<IOptions<StrategyOptions>>().Value.EmaCandleMinutes)));
        services.AddSingleton<IndicatorEngine>();
        services.AddSingleton<SignalEngine>();
        services.AddSingleton<ExpiryResolver>();   // Tuesday-rule fallback
        services.AddSingleton<ExpiryProvider>();   // real expiries from Upstox's contract list
        services.AddSingleton<OptionInstrumentResolver>();
        services.AddSingleton<VirtualPositionTracker>();
        services.AddSingleton<HistoricalSeederService>();

        // Opening Range Breakout + Upside Reversal share the 5-min candle stream.
        services.AddSingleton<FiveMinCandleAggregatorService>();
        services.AddSingleton<BreakoutSignalEngine>();
        services.AddSingleton<BreakoutPositionTracker>();
        services.AddSingleton<ReversalSignalEngine>();
        services.AddSingleton<ReversalPositionTracker>();
        services.AddSingleton<FiveMinHistoricalSeederService>();

        // 3-minute MACD(12,26,9) with dynamic step-trailing SL: its own candle stream.
        services.AddSingleton<ThreeMinCandleAggregatorService>();
        services.AddSingleton<MacdEngine>();
        services.AddSingleton<MacdSignalEngine>();
        services.AddSingleton<MacdPositionTracker>();
        services.AddSingleton<MacdHistoricalSeederService>();

        services.AddSingleton<CprAnalysisService>();

        services.AddHostedService<Worker>();
        return services;
    }

    /// <summary>The Bank Nifty pipeline (code in /BankNifty): shares the feed, token, Telegram, hub and DB with Nifty.</summary>
    public static IServiceCollection AddBankNifty(this IServiceCollection services)
    {
        services.AddSingleton<BankNiftyCandleAggregator>();
        services.AddSingleton<OrbSignalEngine>();
        services.AddSingleton<BankNiftyAlertService>();
        services.AddSingleton<OpeningRangeSeederService>();
        services.AddSingleton<BankNiftyPositionTracker>();
        services.AddSingleton<BankNiftyEmaPipeline>();   // EMA21/50 + ADX strategy on Bank Nifty (same strategy classes as Nifty)

        // One instance, seen both as the background service and as a consumer of the shared feed.
        services.AddSingleton<BankNiftyService>();
        services.AddHostedService(sp => sp.GetRequiredService<BankNiftyService>());
        services.AddSingleton<IMarketFeedConsumer>(sp => sp.GetRequiredService<BankNiftyService>());
        return services;
    }

    /// <summary>SignalR + CORS: the Dashboard's browser JS connects to the hub from another origin/port.</summary>
    public static IServiceCollection AddDashboardChannel(this IServiceCollection services, IConfiguration config)
    {
        services.AddSignalR();
        services.AddCors(options =>
        {
            options.AddPolicy("DashboardCors", policy =>
            {
                var dashboardOrigin = config["NiftyBot:DashboardOrigin"];
                if (string.IsNullOrWhiteSpace(dashboardOrigin))
                {
                    Log.Warning("NiftyBot:DashboardOrigin is not configured -- the Dashboard's browser JS will not be able to connect to the SignalR hub until this is set (e.g. \"http://localhost:5080\").");
                    return;
                }

                // Browsers never send a trailing slash in the Origin header -- WithOrigins needs an EXACT match.
                dashboardOrigin = dashboardOrigin.TrimEnd('/');

                policy.WithOrigins(dashboardOrigin)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();   // required for SignalR
            });
        });
        return services;
    }
}