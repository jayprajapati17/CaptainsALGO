using System.Globalization;
using System.Net;
using AlgoWorker.Configuration;
using AlgoWorker.Models;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace AlgoWorker.BankNifty;
using AlgoWorker.Services;

/// <summary>Telegram alerts for the Bank Nifty bot (same chat as the Nifty bot; every message is tagged BANK NIFTY).</summary>
public sealed class BankNiftyAlertService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string Tag = "🏦 BANK NIFTY";

    private readonly ITelegramBotClient _bot;
    private readonly TelegramOptions _options;
    private readonly ILogger<BankNiftyAlertService> _logger;

    public BankNiftyAlertService(ITelegramBotClient bot, IOptions<TelegramOptions> options, ILogger<BankNiftyAlertService> logger)
    {
        _bot = bot;
        _options = options.Value;
        _logger = logger;
    }

    private async Task SendAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ChatId))
        {
            _logger.LogWarning("Telegram ChatId not configured -- message NOT sent:\n{Text}", text);
            return;
        }
        try
        {
            await _bot.SendTextMessageAsync(chatId: _options.ChatId, text: text, parseMode: ParseMode.Html, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram message.");
        }
    }

    private static string Rs(decimal v) => "₹" + v.ToString("N2", Inv);
    private static string Time(DateTimeOffset dt) => AlgoData.Models.MarketTime.ToIst(dt).ToString("hh:mm tt", Inv);   // exchange (IST) time
    private static string Enc(string s) => WebUtility.HtmlEncode(s);
    private static string Pnl(decimal rupees, decimal percent)
    {
        var sign = rupees >= 0 ? "+" : "-";
        return $"{sign}₹{Math.Abs(rupees).ToString("N0", Inv)} ({sign}{Math.Abs(percent).ToString("0.0", Inv)}%)";
    }
    private static string Emoji(decimal rupees) => rupees >= 0 ? "🟢" : "🔴";
    private static string Opt(OptionInstrument i) => i.Type == OptionType.Call ? "CE" : "PE";
    private static string Title(BankNiftyPosition p) => p.Signal.Direction == BankNiftyDirection.Up ? "ORB BREAKOUT" : "ORB BREAKDOWN";

    public Task SendStatusAsync(string message, CancellationToken ct) => SendAsync($"{Tag}\nℹ️ {Enc(message)}", ct);
    public Task SendWarningAsync(string message, CancellationToken ct) => SendAsync($"{Tag}\n⚠️ {Enc(message)}", ct);

    public Task SendOpeningRangeAsync(OpeningRange r, CancellationToken ct) =>
        SendAsync($"{Tag}\n📐 Opening range {Time(AlgoData.Models.MarketTime.StartOfDay(AlgoData.Models.MarketTime.Today).AddHours(9).AddMinutes(15))}: " +
                  $"High <code>{r.High.ToString("N2", Inv)}</code> · Low <code>{r.Low.ToString("N2", Inv)}</code> " +
                  $"({r.WidthPercent.ToString("0.00", Inv)}%)\n" +
                  (r.IsValid ? "✅ Range OK -- ORB active today." : $"⛔ No ORB trades today: {Enc(r.RejectReason ?? "range filter")}."), ct);

    public Task SendEntryAlertAsync(BankNiftyPosition p, int daysToExpiry, CancellationToken ct)
    {
        var up = p.Signal.Direction == BankNiftyDirection.Up;
        return SendAsync(
            $"{(up ? "🟢" : "🔴")} <b>{Tag} · {Title(p)}</b>\n" +
            $"Index: <code>{p.Signal.SpotAtConfirmation.ToString("N2", Inv)}</code> · {Time(p.Signal.ConfirmedAt)} (level {p.Signal.BrokenLevel.ToString("N2", Inv)})\n" +
            $"<b>BUY {p.Instrument.Strike} {Opt(p.Instrument)}</b> | Exp {p.Instrument.Expiry.ToString("dd-MMM", Inv)} ({daysToExpiry}d)\n" +
            $"Entry <code>{Rs(p.EntryPremium)}</code> | SL <code>{Rs(p.CurrentStopLossPremium)}</code> | Qty {p.LotSize}\n📝 Virtual", ct);
    }

    public Task SendPositionUpdateAsync(BankNiftyPosition p, CancellationToken ct) =>
        SendAsync(
            $"<b>{Tag} · {Title(p)}</b>\n" +
            $"📊 <b>{p.Instrument.Strike} {Opt(p.Instrument)}</b> | Exp {p.Instrument.Expiry.ToString("dd-MMM", Inv)}\n" +
            $"Entry at <code>{Rs(p.EntryPremium)}</code> → <code>{Rs(p.LastKnownPremium)}</code>\n" +
            $"{Emoji(p.PnlRupees)} Profit/Loss: <b>{Pnl(p.PnlRupees, p.PnlPercent)}</b>\n" +
            $"SL <code>{Rs(p.CurrentStopLossPremium)}</code>{(p.TrailingActive ? " (trailing)" : "")}\n" +
            $"Since {Time(p.OpenedAt)}", ct);

    public Task SendExitAlertAsync(BankNiftyPosition p, string reason, CancellationToken ct) =>
        SendAsync(
            $"<b>{Tag} · {Title(p)}</b>\n" +
            $"📊 <b>{p.Instrument.Strike} {Opt(p.Instrument)}</b> | Exp {p.Instrument.Expiry.ToString("dd-MMM", Inv)}\n" +
            $"Reason: {Enc(reason)}\n" +
            $"<code>{Rs(p.EntryPremium)}</code> → <code>{Rs(p.LastKnownPremium)}</code> | {Emoji(p.PnlRupees)} Profit/Loss: <b>{Pnl(p.PnlRupees, p.PnlPercent)}</b>\n" +
            $"Since - {Time(p.OpenedAt)} | Exit - {Time(DateTimeOffset.UtcNow)}", ct);
}
