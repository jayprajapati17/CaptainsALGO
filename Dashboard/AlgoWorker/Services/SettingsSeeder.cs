using System.Globalization;
using System.Reflection;
using AlgoData.Data;
using AlgoData.Models;
using AlgoWorker.Configuration;
using Microsoft.EntityFrameworkCore;

namespace AlgoWorker.Services;

/// <summary>
/// Makes sure the `AppSettings` table exists and contains one row for every public setting of
/// UpstoxOptions / TelegramOptions / StrategyOptions. Rows that already exist are NEVER touched
/// (so values you edited on the Dashboard survive restarts); only missing ones are inserted,
/// using whatever the Worker currently resolved for them (appsettings.json value, else the
/// class default). New settings added in future versions therefore appear automatically.
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

    private static readonly HashSet<string> Secrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApiKey", "ApiSecret", "AccessToken", "BotToken"
    };

    // Values the Worker reads once at startup (engine periods, candle aggregators, the Telegram
    // client, startup seeding, instrument subscription) -- a change needs a Worker restart.
    private static readonly HashSet<string> RestartRequired = new(StringComparer.OrdinalIgnoreCase)
    {
        "Telegram:BotToken",
        "Upstox:NiftyInstrumentKey",
        "Strategy:EmaFastPeriod", "Strategy:EmaMidPeriod", "Strategy:EmaSlowPeriod", "Strategy:AdxPeriod", "Strategy:EmaCandleMinutes",
        "Strategy:MacdFastPeriod", "Strategy:MacdSlowPeriod", "Strategy:MacdSignalPeriod", "Strategy:MacdCandleMinutes",
        "Strategy:SeedLookbackTradingDays", "Strategy:MacdSeedLookbackTradingDays",
        "Strategy:BreakoutStrategyEnabled", "Strategy:MacdStrategyEnabled",
        "Strategy:ReversalEmaFastPeriod", "Strategy:ReversalEmaSlowPeriod", "Strategy:ReversalSeedLookbackTradingDays"
    };

    // Display groups for the Strategy section (anything not listed falls under "General").
    private static readonly Dictionary<string, string> StrategyGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EmaFastPeriod"] = "EMA strategy", ["EmaMidPeriod"] = "EMA strategy", ["EmaSlowPeriod"] = "EMA strategy",
        ["AdxPeriod"] = "EMA strategy", ["EmaCandleMinutes"] = "EMA strategy", ["ConfirmationBars"] = "EMA strategy",
        ["AdxRisingFilter"] = "EMA strategy", ["AlertBothSides"] = "EMA strategy", ["AdxFlatThresholdPoints"] = "EMA strategy",
        ["AdxFlatLookbackBars"] = "EMA strategy", ["AdxDeclineThresholdPoints"] = "EMA strategy",
        ["AdxDeclineConsecutiveBars"] = "EMA strategy", ["SeedLookbackTradingDays"] = "EMA strategy",
        ["ItmStrikeDepth"] = "EMA strategy",

        ["ReversalStrategyEnabled"] = "Upside Reversal strategy",
        ["ReversalSetupAEnabled"] = "Upside Reversal strategy",
        ["ReversalSetupCEnabled"] = "Upside Reversal strategy",
        ["ReversalEarliestEntryTime"] = "Upside Reversal strategy",
        ["ReversalLastEntryTime"] = "Upside Reversal strategy",
        ["ReversalForceExitTime"] = "Upside Reversal strategy",
        ["ReversalEmaFastPeriod"] = "Upside Reversal strategy",
        ["ReversalEmaSlowPeriod"] = "Upside Reversal strategy",
        ["ReversalSeedLookbackTradingDays"] = "Upside Reversal strategy",
        ["ReversalDoubleBottomTolerancePoints"] = "Upside Reversal strategy",
        ["ReversalMinBouncePoints"] = "Upside Reversal strategy",
        ["ReversalConfirmCandles"] = "Upside Reversal strategy",
        ["ReversalMinHigherLows"] = "Upside Reversal strategy",
        ["ReversalSlBufferPoints"] = "Upside Reversal strategy",
        ["ReversalMinRiskReward"] = "Upside Reversal strategy",
        ["ReversalSupportTolerancePoints"] = "Upside Reversal strategy",
        ["ReversalTrendDownPercent"] = "Upside Reversal strategy",
        ["ReversalMaxTradesPerDay"] = "Upside Reversal strategy",
        ["ReversalMaxConsecutiveLosses"] = "Upside Reversal strategy",
        ["BreakoutStrategyEnabled"] = "Breakout (ORB) strategy", ["BreakoutForceExitTime"] = "Breakout (ORB) strategy", ["BreakoutMaxTradesPerDay"] = "Breakout (ORB) strategy",

        ["MacdStrategyEnabled"] = "MACD strategy", ["MacdFastPeriod"] = "MACD strategy", ["MacdSlowPeriod"] = "MACD strategy",
        ["MacdSignalPeriod"] = "MACD strategy", ["MacdCandleMinutes"] = "MACD strategy", ["MacdLotSize"] = "MACD strategy",
        ["MacdForceExitTime"] = "MACD strategy", ["MacdSeedLookbackTradingDays"] = "MACD strategy",

        ["TrailingInitialRiskPoints"] = "Stop-loss / trailing (all strategies)", ["TrailingStepPoints"] = "Stop-loss / trailing (all strategies)",
        ["TrailingStepSlPoints"] = "Stop-loss / trailing (all strategies)", ["TrailingTargetPoints"] = "Stop-loss / trailing (all strategies)",
        ["TrailingTargetLockPoints"] = "Stop-loss / trailing (all strategies)",

        ["CprNarrowThresholdPercent"] = "CPR", ["CprWideThresholdPercent"] = "CPR", ["CprAlertTime"] = "CPR",
    };

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Upstox:ApiKey"] = "Upstox app API key (client id).",
        ["Upstox:ApiSecret"] = "Upstox app API secret.",
        ["Upstox:AccessToken"] = "Fallback token, used only if no token was generated for today via the Dashboard.",
        ["Upstox:RedirectUri"] = "Must exactly match the Redirect URL registered in the Upstox Developer Console.",
        ["Upstox:NiftyInstrumentKey"] = "Upstox instrument key of the Nifty 50 index.",
        ["Telegram:BotToken"] = "Telegram bot token from @BotFather.",
        ["Telegram:ChatId"] = "Chat / channel id the alerts are sent to.",
        ["Strategy:LotSize"] = "Nifty lot size used by the EMA and Breakout strategies.",
        ["Strategy:MacdLotSize"] = "Nifty lot size used by the MACD strategy.",
        ["Strategy:StrikeStepPoints"] = "Strike spacing used for ATM rounding (Nifty = 50).",
        ["Strategy:ItmStrikeDepth"] = "How many strikes in-the-money the EMA current-week leg is.",
        ["Strategy:EmaFastPeriod"] = "Fast EMA period (tracked, not used by signals).",
        ["Strategy:EmaMidPeriod"] = "Mid EMA period -- the line that crosses the slow EMA.",
        ["Strategy:EmaSlowPeriod"] = "Slow EMA period.",
        ["Strategy:AdxPeriod"] = "ADX period.",
        ["Strategy:EmaCandleMinutes"] = "EMA strategy candle timeframe in minutes.",
        ["Strategy:ConfirmationBars"] = "Candles a crossover must hold before it is confirmed.",
        ["Strategy:PositionUpdateIntervalMinutes"] = "How often a 'position running' update is sent to Telegram.",
        ["Strategy:SessionStartTime"] = "Market session start (HH:mm, IST).",
        ["Strategy:SessionEndTime"] = "Market session end (HH:mm, IST).",
        ["Strategy:ReversalStrategyEnabled"] = "Turn the Upside Reversal strategy on/off (needs a Worker restart to take effect on new signals only if changed while a trade is open).",
        ["Strategy:ReversalSetupAEnabled"] = "Setup A: Double Bottom.",
        ["Strategy:ReversalSetupCEnabled"] = "Setup C: Liquidity Sweep + confirmation window.",
        ["Strategy:ReversalEarliestEntryTime"] = "No reversal entries before this time (HH:mm, IST).",
        ["Strategy:ReversalLastEntryTime"] = "No new reversal entries after this time (HH:mm, IST).",
        ["Strategy:ReversalForceExitTime"] = "Open Reversal positions are force-closed at this time (HH:mm, IST).",
        ["Strategy:ReversalEmaFastPeriod"] = "Fast EMA period on the 5-min chart (price must be below it for a setup).",
        ["Strategy:ReversalEmaSlowPeriod"] = "Slow EMA period on the 5-min chart (used by the down-trend-day filter).",
        ["Strategy:ReversalSeedLookbackTradingDays"] = "Previous trading days of 5-min candles replayed at startup.",
        ["Strategy:ReversalDoubleBottomTolerancePoints"] = "Setup A: second low must be within +/- this many points of the first low.",
        ["Strategy:ReversalMinBouncePoints"] = "Setup A: minimum bounce (points) between the two lows.",
        ["Strategy:ReversalConfirmCandles"] = "Setup C: maximum candles to wait for confirmation (3-4 suggested).",
        ["Strategy:ReversalMinHigherLows"] = "Setup C: minimum candles in the window with a low above the sweep low.",
        ["Strategy:ReversalSlBufferPoints"] = "Stop-loss buffer in Nifty points below the setup low (5-10 suggested).",
        ["Strategy:ReversalMinRiskReward"] = "Minimum reward:risk to T2. Setups below this are skipped.",
        ["Strategy:ReversalSupportTolerancePoints"] = "Setup low must be within this many Nifty points of the previous-day low or today's low.",
        ["Strategy:ReversalTrendDownPercent"] = "Skip when EMA fast < EMA slow and Nifty is this % below today's open (strong down-trend day). 0 = filter off.",
        ["Strategy:ReversalMaxTradesPerDay"] = "Maximum Reversal trades opened per day.",
        ["Strategy:ReversalMaxConsecutiveLosses"] = "No more Reversal trades for the day after this many consecutive losses.",
        ["Strategy:BreakoutMaxTradesPerDay"] = "Maximum ORB trades opened per day (re-entries after a stop-loss count). Extra signals are logged as skipped.",
        ["Strategy:BreakoutForceExitTime"] = "Open Breakout positions are force-closed at this time (HH:mm, IST).",
        ["Strategy:MacdForceExitTime"] = "Open MACD positions are force-closed at this time (HH:mm, IST).",
        ["Strategy:MacdCandleMinutes"] = "MACD strategy candle timeframe in minutes.",
        ["Strategy:TrailingInitialRiskPoints"] = "Initial stop-loss, in premium points below entry.",
        ["Strategy:TrailingStepPoints"] = "For every this-many points the price moves up from entry...",
        ["Strategy:TrailingStepSlPoints"] = "...the stop-loss moves up by this many points.",
        ["Strategy:TrailingTargetPoints"] = "First target (points above entry). The position is not closed here; the stop jumps instead.",
        ["Strategy:TrailingTargetLockPoints"] = "When the target is reached the stop moves to Entry + this many points, then keeps stepping.",
    };

    public static async Task SeedAsync(
        IDbContextFactory<NiftyBotDbContext> factory,
        UpstoxOptions upstox, TelegramOptions telegram, StrategyOptions strategy,
        ILogger logger, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlRawAsync(CreateTableSql, ct);

        var existing = await db.AppSettings
            .Select(a => a.Section + ":" + a.Key)
            .ToListAsync(ct);
        var known = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var order = 0;
        foreach (var (section, instance) in new (string, object)[]
                 { ("Upstox", upstox), ("Telegram", telegram), ("Strategy", strategy) })
        {
            foreach (var prop in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite) continue;
                var type = TypeName(prop.PropertyType);
                if (type is null) continue;

                order++;
                var fullKey = $"{section}:{prop.Name}";
                if (known.Contains(fullKey)) continue;

                db.AppSettings.Add(new AppSettingEntity
                {
                    Section = section,
                    Key = prop.Name,
                    Value = Format(prop.GetValue(instance)),
                    ValueType = type,
                    IsSecret = Secrets.Contains(prop.Name),
                    RequiresRestart = RestartRequired.Contains(fullKey),
                    GroupName = section == "Strategy"
                        ? StrategyGroups.GetValueOrDefault(prop.Name, "General")
                        : "General",
                    SortOrder = order,
                    Description = Descriptions.GetValueOrDefault(fullKey, string.Empty),
                    UpdatedAt = DateTimeOffset.Now
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
