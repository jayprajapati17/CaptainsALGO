namespace AlgoWorker.Models;

public enum OptionType { Call, Put }

/// <summary>An ATM/ITM option contract resolved for a specific signal: strike, expiry, and its Upstox instrument key.</summary>
public sealed record OptionInstrument(
    string InstrumentKey,
    string TradingSymbol,
    int Strike,
    OptionType Type,
    DateOnly Expiry);

// >>> NEW: labels which of the two legs (current-week ITM vs next-week ATM) a
// resolved instrument/position belongs to -- used in Telegram messages so the
// two simultaneous alerts/updates are never confused with each other.
public enum OptionLeg { CurrentWeekItm, NextWeekAtm }