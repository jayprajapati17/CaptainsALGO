namespace AlgoWorker.Configuration;

/// <summary>
/// Describes one editable setting for the Dashboard's Settings page. Put it on a public settable property of an
/// options class (UpstoxOptions, TelegramOptions, StrategyOptions, BankNiftyOptions); <see cref="Services.SettingsSeeder"/>
/// then creates the AppSettings row from it (group, description, secret / restart flags). A new setting is therefore
/// one property with one attribute -- no second list to keep in sync.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class SettingAttribute : Attribute
{
    public SettingAttribute(string group = "General", string description = "")
    {
        Group = group;
        Description = description;
    }

    /// <summary>The heading the setting is listed under on the Settings page.</summary>
    public string Group { get; }

    /// <summary>One line shown under the setting's name.</summary>
    public string Description { get; }

    /// <summary>The Worker reads this value only at startup, so a change needs a restart.</summary>
    public bool RestartRequired { get; init; }

    /// <summary>Masked on the Settings page (tokens, secrets).</summary>
    public bool Secret { get; init; }
}
