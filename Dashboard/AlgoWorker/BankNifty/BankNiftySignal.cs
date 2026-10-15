namespace AlgoWorker.BankNifty;

public enum BankNiftyDirection { Up, Down }

/// <summary>A confirmed ORB breakout on the Bank Nifty index.</summary>
/// <param name="CandleOpenTime">Open time of the 5-min candle that closed outside the range.</param>
/// <param name="SpotAtConfirmation">Index close of that candle.</param>
/// <param name="BrokenLevel">The opening-range High (Up) or Low (Down) that was broken.</param>
public sealed record BankNiftySignal(
    BankNiftyDirection Direction,
    DateTimeOffset CandleOpenTime,
    decimal SpotAtConfirmation,
    decimal BrokenLevel)
{
    /// <summary>The moment the candle closed (= when the signal was actually known).</summary>
    public DateTimeOffset ConfirmedAt => CandleOpenTime.AddMinutes(5);
}

/// <summary>Today's opening range (first 5-min candle) and whether it passed the width filter.</summary>
public sealed record OpeningRange(DateOnly Day, decimal High, decimal Low, double WidthPercent, bool IsValid, string? RejectReason);
