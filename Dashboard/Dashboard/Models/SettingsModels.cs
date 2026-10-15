namespace Dashboard.Models;

public sealed class SettingItem
{
    public int Id { get; set; }
    public string Section { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    /// <summary>The value currently stored in the database (differs from Value only when a failed save is being redisplayed).</summary>
    public string Original { get; set; } = string.Empty;
    public string ValueType { get; set; } = "string";
    public bool IsSecret { get; set; }
    public bool RequiresRestart { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }
    public string? Error { get; set; }
}

public sealed class SettingGroup
{
    public string Name { get; set; } = string.Empty;
    public List<SettingItem> Items { get; set; } = new();
}

public sealed class SettingSection
{
    public string Name { get; set; } = string.Empty;
    public List<SettingGroup> Groups { get; set; } = new();
    public bool ReadOnly => Name == "Upstox";
    public int ErrorCount => Groups.Sum(g => g.Items.Count(i => i.Error is not null));
}

public sealed class SettingsViewModel
{
    public string? Error { get; set; }
    public string? Message { get; set; }
    public string? Warning { get; set; }
    public string ActiveTab { get; set; } = "Strategies";
    public List<SettingSection> Sections { get; set; } = new();
}

/// <summary>One setting shown once with a Nifty and a Bank Nifty control side by side. Either side can be missing.</summary>
public sealed record SettingPair(SettingItem? Nifty, SettingItem? BankNifty)
{
    public SettingItem Any => Nifty ?? BankNifty!;
    public bool RestartRequired => (Nifty?.RequiresRestart ?? false) || (BankNifty?.RequiresRestart ?? false);
    public string Description => !string.IsNullOrWhiteSpace(Nifty?.Description) ? Nifty!.Description : BankNifty?.Description ?? string.Empty;
}

public sealed record StrategyCardDef(string Id, string Name, string Tag, string Profile, string? ToggleKey, (string Label, string Key, string Suffix)[] Stats);

/// <summary>Helpers the Settings views use to place each AppSettings row on a tab and pick UI details.</summary>
public static class SettingsUi
{
    public const string TabStrategies = "Strategies";
    public const string TabTelegram = "Telegram";
    public const string TabUpstox = "Upstox";
    public const string TabSession = "Session";

    public static readonly string[] Tabs = { TabStrategies, TabTelegram, TabUpstox, TabSession };

    public static string TabLabel(string tab) => tab == TabSession ? "Session & timing" : tab;

    public static string NormalizeTab(string? tab) => Tabs.Contains(tab) ? tab! : TabStrategies;

    public static string Key(SettingItem i) => i.Section + ":" + i.Key;

    public static string Scope(string section) => section switch { "Strategy" => "Nifty", "BankNifty" => "Bank Nifty", _ => section };

    public static bool IsTrailing(SettingItem i) => i.Key.StartsWith("Trailing", StringComparison.Ordinal);

    public static bool IsSession(SettingItem i) =>
        (i.Section is "Strategy" or "BankNifty")
        && (i.Key.EndsWith("Time", StringComparison.Ordinal) || i.Key == "PositionUpdateIntervalMinutes");

    public static string TabOf(SettingItem i) => i.Section switch
    {
        "Telegram" => TabTelegram,
        "Upstox" => TabUpstox,
        _ => IsSession(i) ? TabSession : TabStrategies
    };

    /// <summary>"TrailingInitialRiskPoints" -> "Trailing initial risk points"; acronyms (EMA, ADX, ORB) stay upper-case.</summary>
    public static string Pretty(string key)
    {
        var words = System.Text.RegularExpressions.Regex.Split(key, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])");
        for (var n = 0; n < words.Length; n++)
        {
            var w = words[n];
            var acronym = w.Length >= 2 && w.All(char.IsUpper);
            if (!acronym) w = n == 0 ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : w.ToLowerInvariant();
            words[n] = w;
        }
        return string.Join(' ', words);
    }

    /// <summary>How much one tap on the +/- buttons changes a numeric setting.</summary>
    public static double StepFor(SettingItem i)
    {
        var k = i.Key;
        if (k.StartsWith("Trailing", StringComparison.Ordinal))
            return k.EndsWith("StepSlPoints", StringComparison.Ordinal) ? 1 : 5;
        if (k.Contains("Rupees", StringComparison.Ordinal)) return 100;
        if (k == "StrikeStepPoints") return 50;
        if (k.Contains("Premium", StringComparison.Ordinal)) return 10;
        if (k.Contains("Percent", StringComparison.Ordinal)) return 0.05;
        return 1;
    }

    /// <summary>
    /// Lines up the Nifty ("Strategy") and Bank Nifty rows by setting key, so a setting that exists for both indices is shown once
    /// with two columns. Nifty order first, then Bank Nifty-only keys.
    /// </summary>
    public static List<SettingPair> Pair(IEnumerable<SettingItem> nifty, IEnumerable<SettingItem> bankNifty)
    {
        var n = nifty.ToList();
        var b = bankNifty.ToList();
        var result = n.Select(x => new SettingPair(x, b.FirstOrDefault(y => y.Key == x.Key))).ToList();
        result.AddRange(b.Where(y => n.All(x => x.Key != y.Key)).Select(y => new SettingPair(null, y)));
        return result;
    }

    public static readonly StrategyCardDef[] Cards =
    {
        new("ema",  "EMA 21/50 + ADX",        "NIFTY · 15 MIN",    "nifty", "Strategy:EmaStrategyEnabled",
            new[] { ("Candle", "Strategy:EmaCandleMinutes", " min"), ("Confirmation bars", "Strategy:ConfirmationBars", ""), ("Lot size", "Strategy:LotSize", "") }),
        new("orb",  "Opening Range Breakout", "NIFTY · 5 MIN",     "nifty", "Strategy:BreakoutStrategyEnabled",
            new[] { ("Max trades / day", "Strategy:BreakoutMaxTradesPerDay", ""), ("Force exit", "Strategy:BreakoutForceExitTime", ""), ("Lot size", "Strategy:LotSize", "") }),
        new("macd", "MACD",                   "NIFTY · 3 MIN",     "nifty", "Strategy:MacdStrategyEnabled",
            new[] { ("Candle", "Strategy:MacdCandleMinutes", " min"), ("Force exit", "Strategy:MacdForceExitTime", ""), ("Lot size", "Strategy:MacdLotSize", "") }),
        new("rev",  "Upside Reversal",        "NIFTY · 5 MIN",     "nifty", "Strategy:ReversalStrategyEnabled",
            new[] { ("Entries from", "Strategy:ReversalEarliestEntryTime", ""), ("Entries until", "Strategy:ReversalLastEntryTime", ""), ("Max trades / day", "Strategy:ReversalMaxTradesPerDay", "") }),
        new("bn",   "Bank Nifty ORB",         "BANK NIFTY · 5 MIN", "bn",   "BankNifty:BreakoutStrategyEnabled",
            new[] { ("Max trades / day", "BankNifty:BreakoutMaxTradesPerDay", ""), ("Force exit", "BankNifty:BreakoutForceExitTime", ""), ("Lot size", "BankNifty:LotSize", "") }),
        new("bnema", "EMA 21/50 + ADX",       "BANK NIFTY · 15 MIN", "bn", "BankNifty:EmaStrategyEnabled",
            new[] { ("Candle", "BankNifty:EmaCandleMinutes", " min"), ("Confirmation bars", "BankNifty:ConfirmationBars", ""), ("Lot size", "BankNifty:LotSize", "") }),
        new("bnw",  "Bank Nifty worker",      "BANK NIFTY · WORKER", "bn", "BankNifty:Enabled",
            new[] { ("Session start", "BankNifty:SessionStartTime", ""), ("Session end", "BankNifty:SessionEndTime", ""), ("Strike step", "BankNifty:StrikeStepPoints", "") })
    };
}