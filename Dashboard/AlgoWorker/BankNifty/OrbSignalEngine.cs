using AlgoWorker.Configuration;
using AlgoWorker.Models;
using Microsoft.Extensions.Options;

namespace AlgoWorker.BankNifty;

using AlgoWorker.Services;

/// <summary>
/// Opening Range Breakout on the Bank Nifty index (5-min candles). The first candle of the day (09:15) is the range;
/// a candle CLOSING above High (Up) / below Low (Down) is a breakout. Nifty-style re-arm: a side fires again only after
/// a candle has closed back inside the range and then closes outside once more. Same rules as the Nifty
/// ORB: no range-width filter. No volume/VWAP/ADX (index feed has none).
/// While <see cref="IsSeeding"/> is true (startup replay) nothing is raised.
/// </summary>
public sealed class OrbSignalEngine
{
    private readonly BankNiftyOptions _options;
    private readonly ILogger<OrbSignalEngine> _logger;

    private DateOnly _day;
    private decimal _high, _low;
    private bool _rangeCaptured;
    private bool _upFired, _downFired;

    public bool IsSeeding { get; set; }
    public event Action<BankNiftySignal>? BreakoutConfirmed;
    public event Action<OpeningRange>? OpeningRangeCaptured;

    public OrbSignalEngine(IOptions<BankNiftyOptions> options, ILogger<OrbSignalEngine> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool HasRange => _rangeCaptured;
    public decimal RangeHigh => _high;
    public decimal RangeLow => _low;

    public void OnCandleClosed(Candle candle)
    {
        var day = DateOnly.FromDateTime(candle.OpenTime.Date);
        if (!_rangeCaptured || day != _day)
        {
            _day = day;
            _high = candle.High;
            _low = candle.Low;
            _rangeCaptured = true;
            _upFired = _downFired = false;

            var width = candle.Open == 0 ? 0 : (double)((candle.High - candle.Low) / candle.Open) * 100.0;   // informational only

            _logger.LogInformation("Opening range {Day}: High={High} Low={Low} width={Width:0.00}%", day, _high, _low, width);
            if (!IsSeeding)
                OpeningRangeCaptured?.Invoke(new OpeningRange(day, _high, _low, width, true, null));
            return;
        }

        // Re-arm once a candle closes back inside the range.
        if (candle.Close <= _high) _upFired = false;
        if (candle.Close >= _low) _downFired = false;

        if (!_options.BreakoutStrategyEnabled) return;

        if (!_upFired && candle.Close > _high)
        {
            _upFired = true;
            Raise(new BankNiftySignal(BankNiftyDirection.Up, candle.OpenTime, candle.Close, _high));
        }
        else if (!_downFired && candle.Close < _low)
        {
            _downFired = true;
            Raise(new BankNiftySignal(BankNiftyDirection.Down, candle.OpenTime, candle.Close, _low));
        }
    }

    private void Raise(BankNiftySignal signal)
    {
        if (IsSeeding)
        {
            _logger.LogDebug("(seeding) ORB {Dir} at {Time} -- historical, not traded.", signal.Direction, signal.CandleOpenTime);
            return;
        }
        _logger.LogInformation("ORB breakout {Dir} confirmed: close {Close} vs level {Level}", signal.Direction, signal.SpotAtConfirmation, signal.BrokenLevel);
        BreakoutConfirmed?.Invoke(signal);
    }
}