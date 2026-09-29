namespace AlgoWorker.Models;

/// <summary>Indicator values as of a specific closed candle.</summary>
public sealed record IndicatorSnapshot(
    DateTimeOffset CandleTime,
    decimal Close,
    double Ema10,
    double Ema21,
    double Ema50,
    double Adx,
    double PlusDi,
    double MinusDi)
{
    public bool Ema21AboveEma50 => Ema21 > Ema50;
    public double Gap2150 => Ema21 - Ema50;
}
