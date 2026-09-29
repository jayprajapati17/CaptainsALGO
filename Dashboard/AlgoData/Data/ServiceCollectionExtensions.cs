using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoData.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="NiftyBotDbContext"/> against SQL Server using the
    /// given connection string. Call this from both NiftyEmaAlertBot (Worker)
    /// and NiftyBot.Dashboard so they always point at the SAME database
    /// (share it via a common config key, e.g. "NiftyBot:ConnectionString"
    /// in each app's appsettings.json).
    ///
    /// >>> NEW: also registers IDbContextFactory&lt;NiftyBotDbContext&gt; --
    /// needed because the Worker Service's position/signal trackers are
    /// SINGLETONS, and EF Core's DbContext is not thread-safe / not meant to
    /// be held long-lived inside a singleton. Singleton consumers should
    /// inject IDbContextFactory&lt;NiftyBotDbContext&gt; and call
    /// CreateDbContext() per operation (short-lived, disposed after each use).
    /// Scoped consumers (e.g. Dashboard MVC controllers, one per HTTP
    /// request) can keep injecting NiftyBotDbContext directly as before --
    /// both registrations coexist without conflict.
    ///
    /// Example connection strings:
    ///   LocalDB:                         "Server=(localdb)\\MSSQLLocalDB;Database=NiftyBot;Trusted_Connection=True;TrustServerCertificate=True"
    ///   Local SQL Server (Windows Auth): "Server=localhost;Database=NiftyBot;Trusted_Connection=True;TrustServerCertificate=True"
    ///   SQL Server (SQL Auth):           "Server=localhost;Database=NiftyBot;User Id=sa;Password=...;TrustServerCertificate=True"
    /// </summary>
    public static IServiceCollection AddNiftyBotDatabase(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<NiftyBotDbContext>(options =>
            options.UseSqlServer(connectionString));

        services.AddDbContext<NiftyBotDbContext>(options =>
            options.UseSqlServer(connectionString));       

        return services;
    }

    /// <summary>
    /// Call once at startup (both apps can call this safely -- it's
    /// idempotent) to make sure the schema exists before anything queries it.
    /// NOTE: this creates tables/indexes/views inside whatever database the
    /// connection string points at -- the DATABASE ITSELF (e.g. "NiftyBot")
    /// must already exist on the SQL Server instance; this does not run
    /// CREATE DATABASE.
    /// </summary>
    public static void EnsureNiftyBotSchema(this IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<NiftyBotDbContext>();
        context.EnsureSchema();
    }
}