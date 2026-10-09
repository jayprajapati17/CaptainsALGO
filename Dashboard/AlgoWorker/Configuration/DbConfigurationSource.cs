using AlgoData.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AlgoWorker.Configuration;

/// <summary>
/// Configuration layer backed by the `AppSettings` table. Added AFTER appsettings.json /
/// environment variables / user-secrets in Program.cs, so any value saved from the Dashboard's
/// Settings page overrides the file. Each row becomes a "Section:Key" entry, so the existing
/// Configure&lt;UpstoxOptions&gt;/&lt;TelegramOptions&gt;/&lt;StrategyOptions&gt; bindings keep working unchanged.
/// </summary>
public sealed class DbConfigurationSource : IConfigurationSource
{
    private readonly string _connectionString;

    /// <summary>The provider created by Build() -- Program.cs keeps this to trigger a reload after the Dashboard saves.</summary>
    public DbConfigurationProvider? Provider { get; private set; }

    public DbConfigurationSource(string connectionString) => _connectionString = connectionString;

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        Provider = new DbConfigurationProvider(_connectionString);
}

public sealed class DbConfigurationProvider : ConfigurationProvider
{
    private readonly string _connectionString;

    public DbConfigurationProvider(string connectionString) => _connectionString = connectionString;

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var options = new DbContextOptionsBuilder<NiftyBotDbContext>()
                .UseSqlServer(_connectionString)
                .Options;
            using var db = new NiftyBotDbContext(options);

            foreach (var row in db.AppSettings.AsNoTracking().ToList())
                data[$"{row.Section}:{row.Key}"] = row.Value;
        }
        catch (Exception ex)
        {
            // First run (table not created yet) or DB temporarily unreachable: fall back to
            // appsettings.json values. Never block startup because of this layer.
            Console.WriteLine($"[Settings] Could not load AppSettings from the database -- using appsettings.json only. ({ex.GetType().Name}: {ex.Message})");
        }

        Data = data;
    }

    /// <summary>Re-read the table and notify option monitors (IOptionsMonitor / change tokens).</summary>
    public void Reload()
    {
        Load();
        OnReload();
    }
}