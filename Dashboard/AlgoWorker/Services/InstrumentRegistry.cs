using AlgoWorker.Configuration;
using Microsoft.Extensions.Options;

namespace AlgoWorker.Services;

/// <summary>
/// Thin lookup layer over the configured "Instruments" list (see
/// InstrumentDefinition.cs). Every other service that needs to know "which
/// instruments exist" or "which instruments run strategy X" should go
/// through this, rather than reading IOptions&lt;List&lt;InstrumentDefinition&gt;&gt;
/// directly, so the enabled/disabled and by-strategy filtering logic lives
/// in exactly one place.
/// </summary>
public sealed class InstrumentRegistry
{
    private readonly List<InstrumentDefinition> _all;

    public InstrumentRegistry(IOptions<List<InstrumentDefinition>> instruments)
    {
        _all = instruments.Value ?? new List<InstrumentDefinition>();
    }

    /// <summary>All enabled instruments, regardless of strategy.</summary>
    public IReadOnlyList<InstrumentDefinition> Enabled => _all.Where(i => i.Enabled).ToList();

    /// <summary>Enabled instruments that run the given strategy (by NiftyBot.Shared.Models.StrategyType name, e.g. "Ema", "Breakout", "Macd", "DailyEma").</summary>
    public IReadOnlyList<InstrumentDefinition> ForStrategy(string strategyName) =>
        _all.Where(i => i.Enabled && i.Strategies.Contains(strategyName, StringComparer.OrdinalIgnoreCase)).ToList();

    public InstrumentDefinition? FindByKey(string key) =>
        _all.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

    public InstrumentDefinition? FindByUpstoxInstrumentKey(string upstoxInstrumentKey) =>
        _all.FirstOrDefault(i => string.Equals(i.UpstoxInstrumentKey, upstoxInstrumentKey, StringComparison.OrdinalIgnoreCase));
}