using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AlgoWorker.Configuration;
using AlgoWorker.Models;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace AlgoWorker.Services;

public sealed class TelegramAlertService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly ITelegramBotClient _bot;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramAlertService> _logger;

    public TelegramAlertService(ITelegramBotClient bot, IOptions<TelegramOptions> options, ILogger<TelegramAlertService> logger)
    {
        _bot = bot;
        _options = options.Value;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Core sender (HTML parse mode)
    // ------------------------------------------------------------------
    private async Task SendAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ChatId))
        {
            _logger.LogWarning("Telegram ChatId not configured -- message NOT sent:\n{Text}", text);
            return;
        }

        try
        {
            // Telegram.Bot 19.x method name (see csproj comment for why we're pinned to 19.x).
            await _bot.SendTextMessageAsync(
                chatId: _options.ChatId,
                text: text,
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram message.");
        }
    }

    // ------------------------------------------------------------------
    // Formatting helpers (InvariantCulture so output never depends on server locale)
    // ------------------------------------------------------------------
    private static string Rs(IFormattable value) => "₹" + value.ToString("N2", Inv);

    private static string Num(IFormattable value, string format) => value.ToString(format, Inv);

    private static string Time(DateTimeOffset dt) => dt.ToString("hh:mm tt", Inv);

    private static string DateTimeFull(DateTimeOffset dt) => dt.ToString("dd-MMM-yyyy hh:mm tt", Inv);

    private static string Exp(OptionInstrument i) => i.Expiry.ToString("dd-MMM", Inv);

    private static string OptType(OptionInstrument i) => i.Type == OptionType.Call ? "CE" : "PE";

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    // "+₹952 (+7.0%)" / "-₹255 (-2.3%)"
    private static string Pnl(decimal rupees, decimal percent)
    {
        var sign = rupees >= 0 ? "+" : "-";
        return $"{sign}₹{Math.Abs(rupees).ToString("N0", Inv)} ({sign}{Math.Abs(percent).ToString("0.0", Inv)}%)";
    }

    private static string PnlEmoji(decimal rupees) => rupees >= 0 ? "🟢" : "🔴";

    // Strategy names shown in Position Update / Exit messages.
    // Direction is derived from the option type: CE = bullish, PE = bearish.
    private static string EmaName(OptionInstrument i) => i.Type == OptionType.Call ? "GOLDEN CROSSOVER" : "DEATH CROSSOVER";

    private static string BreakoutName(OptionInstrument i) => i.Type == OptionType.Call ? "PDH BREAKOUT" : "PDH BREAKDOWN";

    private const string MacdName = "MACD";

    // ------------------------------------------------------------------
    // Shared builders for Position Update and Exit (same layout for every strategy)
    // ------------------------------------------------------------------
    private static string BuildUpdate(
        string strategy, OptionInstrument inst,
        decimal entryPremium, decimal currentPremium,
        decimal pnlRupees, decimal pnlPercent,
        decimal stopLoss, bool trailing, DateTimeOffset openedAt)
    {
        return
            $"<b>{strategy}</b>\n" +
            $"📊 Nifty <b>{inst.Strike} {OptType(inst)}</b> | Exp {Exp(inst)}\n" +
            $"Entry at <code>{Rs(entryPremium)}</code> → <code>{Rs(currentPremium)}</code>\n" +
            $"{PnlEmoji(pnlRupees)} Profit/Loss: <b>{Pnl(pnlRupees, pnlPercent)}</b>\n" +
            $"SL <code>{Rs(stopLoss)}</code>{(trailing ? " (trailing)" : "")}\n" +
            $"Since {Time(openedAt)}";
    }

    private static string BuildExit(
        string strategy, OptionInstrument inst, string reason,
        decimal entryPremium, decimal exitPremium,
        decimal pnlRupees, decimal pnlPercent, DateTimeOffset openedAt)
    {
        return
            $"<b>{strategy}</b>\n" +
            $"📊 Nifty <b>{inst.Strike} {OptType(inst)}</b> | Exp {Exp(inst)}\n" +
            $"Reason: {Enc(reason)}\n" +
            $"<code>{Rs(entryPremium)}</code> → <code>{Rs(exitPremium)}</code> | {PnlEmoji(pnlRupees)} Profit/Loss: <b>{Pnl(pnlRupees, pnlPercent)}</b>\n" +
            $"Since - {Time(openedAt)} | Exit - {Time(DateTimeOffset.Now)}";
    }

    // ------------------------------------------------------------------
    // EMA strategy
    // (Same message for Current-Week and Next-Week legs; the leg parameter is
    //  kept so existing callers don't break, only its instrument is used.)
    // ------------------------------------------------------------------
    public Task SendEntryAlertAsync(TradeSignal signal, ResolvedLeg leg, decimal entryPremium, int lotSize, decimal initialStopLoss, CancellationToken ct)
    {
        var instrument = leg.Instrument;
        var golden = signal.Direction == SignalDirection.GoldenCross;
        var emoji = golden ? "🟢" : "🔴";
        var title = golden ? "GOLDEN CROSSOVER" : "DEATH CROSSOVER";

        // Only shown when catching up on startup, never on a live confirmation.
        var catchUp = signal.IsCatchUp
            ? $" (Originally crossed at {DateTimeFull(signal.ConfirmedAtCandleTime)})"
            : "";

        var text =
            $"{emoji} <b>{title}</b>{catchUp}\n" +
            $"Nifty: <code>{Num(signal.NiftySpotAtConfirmation, "N2")}</code> · {Time(signal.ConfirmedAtCandleTime)}\n" +
            $"<b>BUY {instrument.Strike} {OptType(instrument)}</b> | Exp {Exp(instrument)}\n" +
            $"Entry <code>{Rs(entryPremium)}</code> | SL <code>{Rs(initialStopLoss)}</code>\n" +
            $"ADX <code>{Num(signal.AdxAtConfirmation, "N1")}</code> | 📝 Virtual";

        return SendAsync(text, ct);
    }

    public Task SendPositionUpdateAsync(VirtualPosition position, CancellationToken ct) =>
        SendAsync(BuildUpdate(
            EmaName(position.Instrument), position.Instrument,
            position.EntryPremium, position.LastKnownPremium,
            position.PnlRupees, (decimal)position.PnlPercent,
            position.CurrentStopLossPremium, position.TrailingActive, position.OpenedAt), ct);

    public Task SendExitAlertAsync(VirtualPosition position, string reason, CancellationToken ct) =>
        SendAsync(BuildExit(
            EmaName(position.Instrument), position.Instrument, reason,
            position.EntryPremium, position.LastKnownPremium,
            position.PnlRupees, (decimal)position.PnlPercent, position.OpenedAt), ct);

    // ------------------------------------------------------------------
    // Generic status / warning
    // ------------------------------------------------------------------
    public Task SendStatusAsync(string message, CancellationToken ct) => SendAsync($"ℹ️ {Enc(message)}", ct);

    public Task SendWarningAsync(string message, CancellationToken ct) => SendAsync($"⚠️ {Enc(message)}", ct);

    public Task SendBotStartedAsync(CancellationToken ct) =>
        SendAsync("ℹ️ <b>Bot started</b> · monitoring Nifty\n💬 <i>Don't miss it, the signal is here!</i>", ct);

    // ------------------------------------------------------------------
    // Previous-Day High/Low Breakout strategy
    // ------------------------------------------------------------------
    public Task SendBreakoutEntryAlertAsync(BreakoutSignal signal, OptionInstrument instrument, decimal entryPremium, int lotSize, decimal initialStopLoss, CancellationToken ct)
    {
        var up = signal.Direction == BreakoutDirection.Up;
        var emoji = up ? "🟢" : "🔴";
        var title = up ? "PDH BREAKOUT" : "PDH BREAKDOWN";

        var text =
            $"{emoji} <b>{title}</b>\n" +
            $"Nifty: <code>{Num(signal.NiftySpotAtConfirmation, "N2")}</code> · {Time(signal.ConfirmedAtCandleTime)}\n" +
            $"<b>BUY {instrument.Strike} {OptType(instrument)}</b> | Exp {Exp(instrument)}\n" +
            $"Entry <code>{Rs(entryPremium)}</code> | SL <code>{Rs(initialStopLoss)}</code>\n" +
            $"Intraday only | 📝 Virtual";

        return SendAsync(text, ct);
    }

    public Task SendBreakoutPositionUpdateAsync(BreakoutPosition position, CancellationToken ct) =>
        SendAsync(BuildUpdate(
            BreakoutName(position.Instrument), position.Instrument,
            position.EntryPremium, position.LastKnownPremium,
            position.PnlRupees, (decimal)position.PnlPercent,
            position.CurrentStopLossPremium, position.TrailingActive, position.OpenedAt), ct);

    public Task SendBreakoutExitAlertAsync(BreakoutPosition position, string reason, CancellationToken ct) =>
        SendAsync(BuildExit(
            BreakoutName(position.Instrument), position.Instrument, reason,
            position.EntryPremium, position.LastKnownPremium,
            position.PnlRupees, (decimal)position.PnlPercent, position.OpenedAt), ct);

    // ------------------------------------------------------------------
    // 3-Minute MACD strategy
    // ------------------------------------------------------------------
    public Task SendMacdEntryAlertAsync(MacdSignal signal, OptionInstrument instrument, decimal entryPremium, int lotSize, decimal initialStopLoss, CancellationToken ct)
    {
        var bullish = signal.Direction == MacdDirection.Bullish;
        var emoji = bullish ? "🟢" : "🔴";
        var title = bullish ? "MACD BULLISH" : "MACD BEARISH";

        var text =
            $"{emoji} <b>{title}</b>\n" +
            $"Nifty: <code>{Num(signal.NiftySpotAtConfirmation, "N2")}</code> · {Time(signal.ConfirmedAtCandleTime)}\n" +
            $"<b>BUY {instrument.Strike} {OptType(instrument)}</b> | Exp {Exp(instrument)}\n" +
            $"Entry <code>{Rs(entryPremium)}</code> | SL <code>{Rs(initialStopLoss)}</code>\n" +
            $"MACD {Num(signal.MacdLine, "F2")} / Sig {Num(signal.SignalLine, "F2")} | 📝 Virtual";

        return SendAsync(text, ct);
    }

    public Task SendMacdPositionUpdateAsync(MacdPosition position, CancellationToken ct) =>
        SendAsync(BuildUpdate(
            MacdName, position.Instrument,
            position.EntryPremium, position.LastKnownPremium,
            position.PnlRupees, (decimal)position.PnlPercent,
            position.CurrentStopLossPremium, trailing: false, position.OpenedAt), ct);

    public Task SendMacdTargetHitAlertAsync(MacdPosition position, CancellationToken ct)
    {
        var text =
            $"🎯 <b>MACD TARGET HIT</b> | Nifty <b>{position.Instrument.Strike} {OptType(position.Instrument)}</b>\n" +
            $"Peak <code>{Rs(position.PeakPremium)}</code> | SL now <code>{Rs(position.CurrentStopLossPremium)}</code> 🔒";

        return SendAsync(text, ct);
    }

    public Task SendMacdExitAlertAsync(MacdPosition position, string reason, CancellationToken ct) =>
        SendAsync(BuildExit(
            MacdName, position.Instrument, reason,
            position.EntryPremium, position.LastKnownPremium,
            position.PnlRupees, (decimal)position.PnlPercent, position.OpenedAt), ct);
}