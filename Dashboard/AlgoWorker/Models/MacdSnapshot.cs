namespace AlgoWorker.Models;

/// <summary>MACD(12,26,9) values as of a specific closed 3-min candle.</summary>
public sealed record MacdSnapshot(
    DateTimeOffset CandleTime,
    decimal Close,
    double MacdLine,
    double SignalLine,
    double Histogram)
{
    public bool MacdAboveSignal => MacdLine > SignalLine;
}
