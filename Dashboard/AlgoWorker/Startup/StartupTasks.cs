using AlgoData.Models;
using AlgoWorker.Configuration;
using AlgoWorker.Services;
using AlgoWorker.Services.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

namespace AlgoWorker.Startup;

/// <summary>One-off work done after the host is built and before it starts serving.</summary>
public static class StartupTasks
{
    /// <summary>
    /// Creates the AppSettings table if needed and adds a row for every setting that doesn't have one yet (first run:
    /// copies the current appsettings.json values in; later runs: only brand-new settings), then reloads the DB-backed
    /// configuration layer and rebinds the live options. A failure is logged and the Worker carries on with the
    /// appsettings.json values.
    /// </summary>
    public static async Task SeedSettingsAsync(this WebApplication app, DbConfigurationSource? dbConfigSource)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;

            var sections = SettingsSeeder.Sections(
                sp.GetRequiredService<IOptions<UpstoxOptions>>().Value,
                sp.GetRequiredService<IOptions<TelegramOptions>>().Value,
                sp.GetRequiredService<IOptions<StrategyOptions>>().Value,
                sp.GetRequiredService<IOptions<BankNiftyOptions>>().Value);

            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("AlgoWorker.Startup.Settings");
            var factory = sp.GetRequiredService<IDbContextFactory<AlgoData.Data.NiftyBotDbContext>>();

            await SettingsSeeder.SeedAsync(factory, sections, logger, CancellationToken.None);
            await SchemaMigrator.ApplyAsync(factory, logger, CancellationToken.None);

            dbConfigSource?.Provider?.Reload();
            RebindOptions(app.Services, app.Configuration);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Settings: could not create/seed the AppSettings table -- continuing with appsettings.json values.");
        }
    }

    /// <summary>
    /// The trackers keep open positions in memory only, so any row still marked Open in the DB at process start is an
    /// orphan from a previous run (crash / redeploy / reboot). Close them out now so the Dashboard's Live page never
    /// shows phantom positions that nothing is tracking anymore.
    /// </summary>
    public static async Task CloseOrphanedPositionsAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var positions = scope.ServiceProvider.GetRequiredService<IPositionRepository>();
        const string reason = "Worker restarted -- position no longer tracked";
        await positions.CloseOrphanedOpenPositionsAsync(reason, CancellationToken.None, Underlyings.Nifty50);
        await positions.CloseOrphanedOpenPositionsAsync(reason, CancellationToken.None, Underlyings.BankNifty);
    }

    /// <summary>
    /// Services hold on to IOptions&lt;T&gt;.Value (one shared instance), so updating that instance IN PLACE makes the new
    /// values visible everywhere immediately. IOptionsMonitor&lt;T&gt; consumers (the REST client) refresh through the
    /// configuration reload token. Anything the Worker only reads at startup is flagged RequiresRestart in the table.
    /// </summary>
    public static void RebindOptions(IServiceProvider sp, IConfiguration config)
    {
        config.GetSection(UpstoxOptions.SectionName).Bind(sp.GetRequiredService<IOptions<UpstoxOptions>>().Value);
        config.GetSection(TelegramOptions.SectionName).Bind(sp.GetRequiredService<IOptions<TelegramOptions>>().Value);
        config.GetSection(StrategyOptions.SectionName).Bind(sp.GetRequiredService<IOptions<StrategyOptions>>().Value);
        config.GetSection(BankNiftyOptions.SectionName).Bind(sp.GetRequiredService<IOptions<BankNiftyOptions>>().Value);
    }
}
