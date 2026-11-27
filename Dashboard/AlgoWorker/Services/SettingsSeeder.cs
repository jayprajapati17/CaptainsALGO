using System.Globalization;
using System.Reflection;
using AlgoData.Data;
using AlgoData.Models;
using AlgoWorker.Configuration;
using Microsoft.EntityFrameworkCore;

namespace AlgoWorker.Services;

/// <summary>
/// Makes sure the `AppSettings` table exists and contains one row for every public setting of the options classes
/// (Upstox, Telegram, Strategy, Pcr, EmaCross). Rows that already exist are NEVER touched (so values you edited on the
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

    /// <summary>Every "Section:Key" whose AppSettings row is deleted at startup because the setting no longer exists.</summary>
    private static readonly string[] ObsoleteRows =
    {
        "Strategy:AdxRisingFilter",                      // never read by any strategy code
        // Liquidity Sweep (CE): Setup A (double bottom), the fixed-target rules and the old EMA 21/50 settings were removed.
        "Strategy:ReversalSetupAEnabled", "Strategy:ReversalSetupCEnabled", "Strategy:ReversalDoubleBottomTolerancePoints",
        "Strategy:ReversalMinBouncePoints", "Strategy:ReversalMinRiskReward", "Strategy:ReversalEmaFastPeriod", "Strategy:ReversalEmaSlowPeriod"
    };

    /// <summary>Whole sections that no longer exist: all their rows are deleted at startup (the Bank Nifty pipeline was removed).</summary>
    private static readonly string[] ObsoleteSections = { "BankNifty" };

    /// <summary>The (section name, live options instance) pairs that make up the Settings page, in display order.</summary>
    public static IReadOnlyList<(string Section, object Options)> Sections(
        UpstoxOptions upstox, TelegramOptions telegram, StrategyOptions strategy, PcrOptions pcr, EmaCrossOptions emaCross) => new (string, object)[]
    {
        (UpstoxOptions.SectionName, upstox),
        (TelegramOptions.SectionName, telegram),
        (StrategyOptions.SectionName, strategy),
        (PcrOptions.SectionName, pcr),
        (EmaCrossOptions.SectionName, emaCross)
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

        // Rows left behind by settings that no longer exist (the whole BankNifty section, AdxRisingFilter): delete them so the
        // Settings page doesn't show controls that do nothing.
        var obsolete = ObsoleteRows.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = (await db.AppSettings.ToListAsync(ct))
            .Where(a => obsolete.Contains(a.Section + ":" + a.Key) || ObsoleteSections.Contains(a.Section)).ToList();
        if (stale.Count > 0)
        {
            db.AppSettings.RemoveRange(stale);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Settings: removed {Count} obsolete row(s).", stale.Count);
        }

        // Keep the text of existing rows in step with the code (group, description, restart flag, order); the VALUE is never touched.
        var rows = (await db.AppSettings.ToListAsync(ct)).ToDictionary(a => a.Section + ":" + a.Key, StringComparer.OrdinalIgnoreCase);
        var synced = 0;

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
                var meta = prop.GetCustomAttribute<SettingAttribute>();
                if (known.Contains($"{section}:{prop.Name}"))
                {
                    if (meta is not null && rows.TryGetValue($"{section}:{prop.Name}", out var row)
                        && (row.GroupName != meta.Group || row.Description != meta.Description || row.RequiresRestart != meta.RestartRequired))
                    {
                        row.GroupName = meta.Group;
                        row.Description = meta.Description;
                        row.RequiresRestart = meta.RestartRequired;
                        synced++;
                    }
                    continue;
                }

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

        if (added > 0 || synced > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Settings: seeded {Added} new row(s), refreshed the text of {Synced} existing row(s).", added, synced);
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