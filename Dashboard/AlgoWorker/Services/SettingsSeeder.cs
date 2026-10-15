using System.Globalization;
using System.Reflection;
using AlgoData.Data;
using AlgoData.Models;
using AlgoWorker.Configuration;
using Microsoft.EntityFrameworkCore;

namespace AlgoWorker.Services;

/// <summary>
/// Makes sure the `AppSettings` table exists and contains one row for every public setting of the options classes
/// (Upstox, Telegram, Strategy, BankNifty). Rows that already exist are NEVER touched (so values you edited on the
/// Dashboard survive restarts); only missing ones are inserted, using whatever the Worker currently resolved for them
/// (appsettings.json value, else the class default). A new setting is just a new property: its group, description and
/// secret / restart flags come from its <see cref="SettingAttribute"/>, so it appears on the Settings page automatically.
/// </summary>
public static class SettingsSeeder
{
    private const string CreateTableSql = @"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AppSettings')
BEGIN
    CREATE TABLE AppSettings (
        Id               INT IDENTITY(1,1) PRIMARY KEY,
        Section          NVARCHAR(100)   NOT NULL,
        [Key]            NVARCHAR(150)   NOT NULL,
        [Value]          NVARCHAR(MAX)   NULL,
        ValueType        NVARCHAR(20)    NOT NULL CONSTRAINT DF_AppSettings_ValueType DEFAULT ('string'),
        IsSecret         BIT             NOT NULL CONSTRAINT DF_AppSettings_IsSecret DEFAULT (0),
        RequiresRestart  BIT             NOT NULL CONSTRAINT DF_AppSettings_RequiresRestart DEFAULT (0),
        GroupName        NVARCHAR(100)   NOT NULL CONSTRAINT DF_AppSettings_GroupName DEFAULT (''),
        SortOrder        INT             NOT NULL CONSTRAINT DF_AppSettings_SortOrder DEFAULT (0),
        [Description]    NVARCHAR(500)   NOT NULL CONSTRAINT DF_AppSettings_Description DEFAULT (''),
        UpdatedAt        DATETIMEOFFSET  NOT NULL CONSTRAINT DF_AppSettings_UpdatedAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT UQ_AppSettings_Section_Key UNIQUE (Section, [Key])
    );
END";

    /// <summary>Bank Nifty keys that were removed or renamed when the ORB was aligned with the Nifty ORB.</summary>
    private static readonly string[] ObsoleteKeys =
    {
        "EntryStartTime", "EntryEndTime", "ForceExitTime", "MaxDaysToExpiry",
        "TargetPremium", "ExpiryDayTargetPremium", "TargetPremiumTolerance", "MaxPremium", "MinPremium", "StrikeSearchRangePoints",
        "MaxTradesPerDay", "MaxConsecutiveLosses", "WeeklyLossLimitRupees",
        "OrbStrategyEnabled", "OrbMinRangePercent", "OrbMaxRangePercent", "OrbStructureExitEnabled"
    };

    /// <summary>The (section name, live options instance) pairs that make up the Settings page, in display order.</summary>
    public static IReadOnlyList<(string Section, object Options)> Sections(
        UpstoxOptions upstox, TelegramOptions telegram, StrategyOptions strategy, BankNiftyOptions bankNifty) => new (string, object)[]
    {
        (UpstoxOptions.SectionName, upstox),
        (TelegramOptions.SectionName, telegram),
        (StrategyOptions.SectionName, strategy),
        (BankNiftyOptions.SectionName, bankNifty)
    };

    public static async Task SeedAsync(
        IDbContextFactory<NiftyBotDbContext> factory,
        IReadOnlyList<(string Section, object Options)> sections,
        ILogger logger, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlRawAsync(CreateTableSql, ct);

        var existing = await db.AppSettings
            .Select(a => a.Section + ":" + a.Key)
            .ToListAsync(ct);
        var known = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        // Rows left behind by settings that no longer exist (the Bank Nifty ORB now follows the Nifty rules): remove them
        // so the Settings page doesn't show controls that do nothing.
        var obsolete = ObsoleteKeys.Select(k => BankNiftyOptions.SectionName + ":" + k).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = await db.AppSettings
            .Where(a => a.Section == BankNiftyOptions.SectionName)
            .ToListAsync(ct);
        stale = stale.Where(a => obsolete.Contains(a.Section + ":" + a.Key)).ToList();
        if (stale.Count > 0)
        {
            db.AppSettings.RemoveRange(stale);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Settings: removed {Count} obsolete Bank Nifty row(s).", stale.Count);
        }

        var added = 0;
        var order = 0;
        foreach (var (section, instance) in sections)
        {
            foreach (var prop in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite) continue;
                var type = TypeName(prop.PropertyType);
                if (type is null) continue;

                order++;
                if (known.Contains($"{section}:{prop.Name}")) continue;

                var meta = prop.GetCustomAttribute<SettingAttribute>();
                db.AppSettings.Add(new AppSettingEntity
                {
                    Section = section,
                    Key = prop.Name,
                    Value = Format(prop.GetValue(instance)),
                    ValueType = type,
                    IsSecret = meta?.Secret ?? false,
                    RequiresRestart = meta?.RestartRequired ?? false,
                    GroupName = meta?.Group ?? "General",
                    SortOrder = order,
                    Description = meta?.Description ?? string.Empty,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                added++;
            }
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Settings: seeded {Count} new row(s) into AppSettings from appsettings.json / defaults.", added);
        }
    }

    private static string? TypeName(Type t)
    {
        if (t == typeof(string)) return "string";
        if (t == typeof(int)) return "int";
        if (t == typeof(double)) return "double";
        if (t == typeof(bool)) return "bool";
        return null;
    }

    private static string? Format(object? v) => v switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString()
    };
}