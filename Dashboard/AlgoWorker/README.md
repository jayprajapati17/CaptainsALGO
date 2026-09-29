# Nifty EMA Alert Bot

.NET 8 Worker Service that watches Nifty 50 in real time (Upstox WebSocket),
detects confirmed EMA21/50 crossovers using the backtested rules from
`Nifty_EMA_Alert_Bot_Design.md`, and sends Telegram alerts with **virtual
(paper) P&L tracking** on the corresponding next-week-expiry ATM option.
**No real orders are ever placed.**

## 1. Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- An Upstox developer account with API key/secret and a daily access token
  ([Upstox Developer Console](https://account.upstox.com/developer/apps))
- A Telegram bot token (via [@BotFather](https://t.me/BotFather)) and your chat ID

## 2. One-time setup: Protobuf decoding

Upstox's live feed is Protobuf-encoded and Upstox owns/updates the schema.
**Read `Services/Decoding/README.md` first** -- you need to download their
current `.proto` file and wire it up before the bot can actually decode ticks.
Until that's done, the bot will log a clear error instead of silently
processing garbage data.

## 3. Configure `appsettings.json`

Fill in (or better, use `dotnet user-secrets` locally so tokens never get
committed to source control):

```
dotnet user-secrets init
dotnet user-secrets set "Upstox:ApiKey" "..."
dotnet user-secrets set "Upstox:ApiSecret" "..."
dotnet user-secrets set "Upstox:AccessToken" "..."
dotnet user-secrets set "Telegram:BotToken" "..."
dotnet user-secrets set "Telegram:ChatId" "..."
```

## 4. Daily token refresh (manual, per your choice in the design doc)

Upstox access tokens expire every day (~3:30 AM). Each morning:

1. Go through Upstox's login flow to get a fresh token.
2. Update it via `dotnet user-secrets set "Upstox:AccessToken" "<new-token>"`
   (or directly in `appsettings.json` if you're not using user-secrets).
3. **No restart needed** -- the config is set to reload on change, and
   `UpstoxRestClient` reads the token fresh on every request via
   `IOptionsMonitor<UpstoxOptions>`.
4. If the bot is running with a stale/invalid token, it will send you a
   Telegram warning ("token expired/rejected") and keep retrying every 2
   minutes until you update it -- it won't crash or need a restart.

## 5. Build & run

```
dotnet restore
dotnet build
dotnet run
```

Logs go to both the console and `logs/nifty-bot-YYYY-MM-DD.log` (rolling
daily, 30 days retained).

## 6. What the bot actually does (quick recap)

- Warms up EMA10/21/50 + ADX from ~20 trading days of historical 15-min
  candles at the start of each trading day.
- Streams live Nifty 50 ticks over WebSocket, builds 15-min candles.
- On a confirmed (1-hour-held) EMA21/50 crossover:
  - Golden Cross + ADX rising -> **HIGH confidence** long alert (buy CE)
  - Golden Cross without ADX rising -> **MEDIUM confidence**
  - Death Cross (either way) -> **LOW confidence** (no proven backtest edge, informational)
- Resolves the ATM strike for **next week's** Tuesday expiry (never current week).
- Opens a **virtual position**, subscribes to that option's live price, and
  sends periodic P&L updates + a final exit alert once the ADX-flat/declining
  rule fires (or a safety-net trigger: opposite crossover / expiry day / EMA50 stop).

## 7. Known limitations (be aware of these before trusting it with real money)

- Backtest edge is small and only borderline-significant (p=0.093 best case,
  Golden+ADX-rising). This is a decision-support alert tool, not a guarantee.
- Death Cross / short-side signals have **no proven statistical edge** in the
  5-year backtest -- alerted only for information, per your request to see both sides.
- Expiry resolution assumes Tuesday expiry and does not know about NSE
  holidays that shift expiry to Monday -- cross-check around holiday weeks.
- Protobuf decoding requires the manual one-time setup in step 2.
- This bot has not been run against a live Upstox account by me -- test
  thoroughly in a quiet/paper context before trusting the alerts operationally.
