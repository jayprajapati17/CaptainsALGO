namespace AlgoWorker.Configuration;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    [Setting("General", "Telegram bot token from @BotFather.", RestartRequired = true, Secret = true)]
    public string BotToken { get; set; } = string.Empty;
    [Setting("General", "Chat / channel id the alerts are sent to.")]
    public string ChatId { get; set; } = string.Empty;
}
