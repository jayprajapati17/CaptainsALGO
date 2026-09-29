using Microsoft.Extensions.Logging;
using AlgoWorker.Models;

namespace AlgoWorker.Services;

/// <summary>
/// 3-Minute MACD(12,26,9) strategy, per the doc:
///   - Zero-line filter (directional bias): bullish entries only considered
///     when MACD line AND Signal line are both at/above zero; bearish entries
///     only when both are at/below zero. This also does double duty as the
///     doc's "avoid choppy markets when MACD hugs the zero line" guidance --
///     a crossover while the two lines straddle opposite sides of zero simply
///     fails the bias check and is skipped.
///   - Crossover trigger: Fast MACD crosses the Signal line while the bias
///     check above holds. Unlike the EMA21/50 strategy, there is NO multi-candle
///     hold/confirmation -- the doc says to enter on the close of the trigger candle itself.
/// </summary>
public sealed class MacdSignalEngine
{
    private readonly ILogger<MacdSignalEngine> _logger;
    private MacdSnapshot? _previous;

    public event Action<MacdSignal>? MacdSignalConfirmed;

    // >>> NEW: same purpose as SignalEngine.IsSeeding -- set true while replaying
    // historical 3-min candles at startup, so past crossovers are logged (state
    // still advances correctly) but never fire a real alert / open a position.
    public bool IsSeeding { get; set; }

    public MacdSignalEngine(ILogger<MacdSignalEngine> logger)
    {
        _logger = logger;
    }

    /// <summary>Feed every CLOSED 3-min MACD snapshot here, in chronological order (null snapshots -- still warming up -- should not be passed in).</summary>
    public void OnMacdSnapshot(MacdSnapshot current)
    {
        var previous = _previous;
        _previous = current; // always advance, even if we don't fire below

        if (previous is null)
            return; // need at least one prior snapshot to detect a cross

        var crossedUp = !previous.MacdAboveSignal && current.MacdAboveSignal;
        var crossedDown = previous.MacdAboveSignal && !current.MacdAboveSignal;

        if (!crossedUp && !crossedDown)
            return;

        var isBullish = crossedUp && IsBullishBias(current);
        var isBearish = crossedDown && IsBearishBias(current);

        if (!isBullish && !isBearish)
        {
            _logger.LogDebug("MACD crossover at {Time} skipped -- zero-line bias not confirmed (MACD {Macd:F2}, Signal {Signal:F2}, likely choppy/hugging zero).",
                current.CandleTime, current.MacdLine, current.SignalLine);
            return;
        }

        var direction = isBullish ? MacdDirection.Bullish : MacdDirection.Bearish;

        if (IsSeeding)
        {
            _logger.LogDebug("(seeding) MACD {Direction} confirmed at {Time} -- not alerting, historical replay only.", direction, current.CandleTime);
            return;
        }

        _logger.LogInformation("MACD {Direction} crossover confirmed at {Time}: MACD {Macd:F2}, Signal {Signal:F2}",
            direction, current.CandleTime, current.MacdLine, current.SignalLine);
        MacdSignalConfirmed?.Invoke(new MacdSignal(direction, current.CandleTime, current.Close, current.MacdLine, current.SignalLine));
    }

    /// <summary>Both lines at/above zero -- the doc's "MACD lines operating above the Zero Line".</summary>
    private static bool IsBullishBias(MacdSnapshot s) => s.MacdLine >= 0 && s.SignalLine >= 0;

    /// <summary>Both lines at/below zero.</summary>
    private static bool IsBearishBias(MacdSnapshot s) => s.MacdLine <= 0 && s.SignalLine <= 0;

    /// <summary>Does the CURRENT (already-passed-in) snapshot represent an opposite-direction crossover to an open position? Used by the tracker's exit check.</summary>
    public static bool IsOppositeCrossover(MacdDirection openDirection, MacdSnapshot previous, MacdSnapshot current)
    {
        var crossedUp = !previous.MacdAboveSignal && current.MacdAboveSignal;
        var crossedDown = previous.MacdAboveSignal && !current.MacdAboveSignal;
        return openDirection == MacdDirection.Bullish ? crossedDown : crossedUp;
    }
}
