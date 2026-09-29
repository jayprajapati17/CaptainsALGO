using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AlgoWorker.Configuration;
using AlgoWorker.Models;

namespace AlgoWorker.Services;

/// <summary>
/// Computes tomorrow's Central Pivot Range + standard pivot S/R levels from
/// today's completed High/Low/Close, and classifies the width into a
/// Narrow/Wide/Normal "reading" -- narrower CPR historically tends to precede
/// a bigger-range trending day, wider CPR a more sideways one (this is the
/// "trading signal" requested: a possibility read for tomorrow, not a
/// tracked virtual position -- there is nothing to enter until tomorrow's
/// open, which is a different mechanism from this end-of-day info alert).
/// </summary>
public sealed class CprAnalysisService
{
    private readonly StrategyOptions _options;
    private readonly ILogger<CprAnalysisService> _logger;

    public CprAnalysisService(IOptions<StrategyOptions> options, ILogger<CprAnalysisService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public CprLevels Compute(decimal todayHigh, decimal todayLow, decimal todayClose, DateOnly forTradingDay)
    {
        var pivot = (todayHigh + todayLow + todayClose) / 3m;
        var bc = (todayHigh + todayLow) / 2m;
        var tc = (2m * pivot) - bc;
        var r1 = (2m * pivot) - todayLow;
        var s1 = (2m * pivot) - todayHigh;
        var r2 = pivot + (todayHigh - todayLow);
        var s2 = pivot - (todayHigh - todayLow);

        var width = Math.Abs(tc - bc);
        var widthPercent = (double)(width / todayClose) * 100.0;

        var reading = widthPercent < _options.CprNarrowThresholdPercent
            ? "Narrow CPR -- Trending day likely, bigger range moves expected"
            : widthPercent > _options.CprWideThresholdPercent
                ? "Wide CPR -- Sideways/Range-bound day likely, mean-reversion between S1-R1 more probable"
                : "Normal CPR -- Moderate movement expected";

        var biasNote = todayClose > pivot
            ? "Today closed ABOVE pivot -- mild bullish lean for tomorrow's open"
            : todayClose < pivot
                ? "Today closed BELOW pivot -- mild bearish lean for tomorrow's open"
                : "Today closed AT pivot -- no directional lean";

        var levels = new CprLevels(
            ForTradingDay: forTradingDay,
            SourceHigh: todayHigh,
            SourceLow: todayLow,
            SourceClose: todayClose,
            Pivot: pivot,
            Tc: tc,
            Bc: bc,
            R1: r1,
            S1: s1,
            R2: r2,
            S2: s2,
            WidthPercent: widthPercent,
            Reading: reading,
            BiasNote: biasNote);

        _logger.LogInformation("CPR computed for {Day}: Pivot={Pivot}, TC={Tc}, BC={Bc}, Width%={Width:N3}, Reading={Reading}",
            forTradingDay, pivot, tc, bc, widthPercent, reading);

        return levels;
    }
}