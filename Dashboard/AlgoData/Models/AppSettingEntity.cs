namespace AlgoData.Models;

/// <summary>
/// Maps 1:1 to the `AppSettings` table in schema-sqlserver.sql. One row per
/// setting that used to live only in the Worker's appsettings.json (Upstox,
/// Telegram, Strategy sections). The Worker loads these at startup (and again
/// on "Save" from the Dashboard) as a configuration layer ON TOP of
/// appsettings.json, so a value saved here wins over the file.
/// </summary>
public sealed class AppSettingEntity
{
    public int Id { get; set; }

    /// <summary>"Upstox", "Telegram" or "Strategy" -- the configuration section the key belongs to.</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>Property name inside the section, e.g. "TrailingInitialRiskPoints".</summary>
    public string Key { get; set; } = string.Empty;

    public string? Value { get; set; }

    /// <summary>"string", "int", "double" or "bool" -- tells the Dashboard which input to render and how to validate.</summary>
    public string ValueType { get; set; } = "string";

    /// <summary>Shown masked (password input) on the Dashboard.</summary>
    public bool IsSecret { get; set; }

    /// <summary>True when the Worker only reads this value at startup (a change needs a Worker restart to take effect).</summary>
    public bool RequiresRestart { get; set; }

    /// <summary>Display grouping inside a section (e.g. "Trailing stop-loss", "MACD strategy").</summary>
    public string GroupName { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public string Description { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}