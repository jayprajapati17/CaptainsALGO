using AlgoWorker.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace AlgoWorker.Startup;

/// <summary>Configuration sources and the default listen address / Dashboard origin.</summary>
public static class ConfigurationSetup
{
    /// <summary>
    /// appsettings.json, environment, user-secrets, then the DB-backed layer LAST so a value saved from the Dashboard's
    /// Settings page overrides the same key in the files. NiftyBot:ConnectionString itself (and Instruments, Serilog)
    /// stay in appsettings.json -- the DB address can't be read from the DB. Returns the DB source (null when no
    /// connection string is configured) so the reload endpoint can refresh it.
    /// </summary>
    public static DbConfigurationSource? AddWorkerConfiguration(this WebApplicationBuilder builder)
    {
        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        builder.Configuration.AddJsonFile(
            $"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddUserSecrets<Program>(optional: true);

        DbConfigurationSource? dbConfigSource = null;
        var bootstrapConnectionString = builder.Configuration["NiftyBot:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(bootstrapConnectionString))
        {
            dbConfigSource = new DbConfigurationSource(bootstrapConnectionString);
            ((IConfigurationBuilder)builder.Configuration).Add(dbConfigSource);
        }

        // Listen where everything else expects the Worker (Dashboard's WorkerBaseUrl, the Upstox OAuth RedirectUri).
        // An explicit "Urls" / "Kestrel:Endpoints" setting or ASPNETCORE_URLS still wins.
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])
            && !builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
        {
            builder.WebHost.UseUrls("http://localhost:5050");
        }

        // Default CORS origin for the Dashboard's browser-side SignalR connection.
        if (string.IsNullOrWhiteSpace(builder.Configuration["NiftyBot:DashboardOrigin"]))
        {
            builder.Configuration["NiftyBot:DashboardOrigin"] = "http://localhost:5080";
        }

        return dbConfigSource;
    }
}
