namespace Dashboard.Models;

public sealed class SettingItem
{
    public int Id { get; set; }
    public string Section { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
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
    public string ActiveTab { get; set; } = "Telegram";
    public List<SettingSection> Sections { get; set; } = new();
}
