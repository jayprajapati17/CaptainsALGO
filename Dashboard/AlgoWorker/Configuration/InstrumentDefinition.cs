namespace AlgoWorker.Configuration;

/// <summary>Whether an instrument's option chain has a weekly expiry cycle (Nifty, Sensex) or monthly-only (Bank Nifty since Nov-2024, and every individual stock, always).</summary>
public enum ExpiryCycle
{
    Weekly,
    Monthly
}

/// <summary>
/// How a position's strike (and therefore its premium cost) is chosen.
///   FixedLot:      always the ATM/ITM strike as computed by the strategy, 1 lot, premium cost is whatever it is (today's Nifty/Sensex behavior).
///   CapitalBudget: pick the strike closest to ATM (moving OTM if needed) whose premium x LotSize is closest to CapitalBudgetRupees WITHOUT exceeding it. Always 1 lot.
/// </summary>
public enum PositionSizingMode
{
    FixedLot,
    CapitalBudget
}

/// <summary>
/// One entry in the root-level "Instruments" config array (appsettings.json) --
/// everything the bot needs to trade a given underlying generically, instead
/// of the old hardcoded-to-Nifty assumptions. Bound directly as
/// IOptions&lt;List&lt;InstrumentDefinition&gt;&gt; (see Program.cs) -- a JSON array
/// at the config root binds to a List&lt;T&gt; without any wrapper object needed.
/// </summary>
public sealed class InstrumentDefinition
{
    /// <summary>Short internal key used in logs, DB rows, Telegram messages, and the Dashboard -- e.g. "NIFTY50", "BANKNIFTY", "RELIANCE". Must be unique.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The Upstox instrument key for the underlying itself (index or equity spot), e.g. "NSE_INDEX|Nifty 50", "NSE_EQ|INE002A01018" (Reliance).</summary>
    public string UpstoxInstrumentKey { get; set; } = string.Empty;

    /// <summary>"NSE" or "BSE" -- which exchange's option chain to resolve contracts from.</summary>
    public string Exchange { get; set; } = "NSE";

    public int LotSize { get; set; }

    /// <summary>Gap between adjacent strikes for this instrument (50 for Nifty, 100 for Bank Nifty/Sensex, varies a lot per stock).</summary>
    public int StrikeStep { get; set; }

    public ExpiryCycle ExpiryCycle { get; set; } = ExpiryCycle.Monthly;

    /// <summary>Only used when ExpiryCycle = Weekly -- which day the weekly contract expires on (Nifty = Tuesday, Sensex = Thursday, as of 2026).</summary>
    public DayOfWeek WeeklyExpiryDay { get; set; } = DayOfWeek.Tuesday;

    /// <summary>Which strategies this instrument runs, by NiftyBot.Shared.Models.StrategyType name -- e.g. ["Ema","Breakout"]. An instrument not listed here for a given strategy is simply skipped by it.</summary>
    public List<string> Strategies { get; set; } = new();

    public PositionSizingMode Sizing { get; set; } = PositionSizingMode.FixedLot;

    /// <summary>Only used when Sizing = CapitalBudget -- target rupees to deploy per trade (1 lot at the nearest-fit strike, never exceeds this).</summary>
    public decimal CapitalBudgetRupees { get; set; }

    /// <summary>Set false to keep an instrument configured but temporarily out of rotation, without deleting its config.</summary>
    public bool Enabled { get; set; } = true;
}