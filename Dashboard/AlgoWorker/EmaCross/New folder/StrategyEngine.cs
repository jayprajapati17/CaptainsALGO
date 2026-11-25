namespace AlgoWorker.EmaCross.Core;

/// <summary>
/// The EMA 9/21 crossover engine, following the backtest-parity rules in section 2.3 exactly. It runs on candle CLOSE;
/// entries and crossover exits are fill-at-next-open, so they are held as <see cref="PendingEntry"/> /
/// <see cref="PendingExit"/> until the next candle arrives. A live executor reads those two properties right after
/// <see cref="OnCandleClosed"/> to know what to do at the next open.
/// </summary>
public sealed class StrategyEngine
{
    private readonly StrategyConfig _cfg;
    private readonly IEntryFilter _filter;
    private readonly Ema _fast, _slow;
    private double _prevFast, _prevSlow;
    private int _count;
    private Side? _pendingEntry;
    private bool _pendingExit;
    private Position? _pos;

    public StrategyEngine(StrategyConfig cfg, IEntryFilter? filter = null)
    {
        _cfg = cfg;
        _filter = filter ?? new NoFilter();
        _fast = new Ema(cfg.FastPeriod);
        _slow = new Ema(cfg.SlowPeriod);
    }

    public List<Trade> Trades { get; } = new();
    public Position? OpenPosition => _pos;

    /// <summary>Set on a candle close: open this side at the NEXT candle's open.</summary>
    public Side? PendingEntry => _pendingEntry;

    /// <summary>Set on a candle close: close the open position at the NEXT candle's open (opposite crossover).</summary>
    public bool PendingExit => _pendingExit;

    public double? FastEma => _fast.Value;
    public double? SlowEma => _slow.Value;

    /// <summary>Candles fed so far.</summary>
    public int CandleCount => _count;

    public void OnCandleClosed(Bar c, FilterContext ctx)
    {
        double f = _fast.Update(c.Close);
        double s = _slow.Update(c.Close);
        _count++;
        bool warm = _count > _cfg.SlowPeriod + 1;   // candle index 0..21 never signal

        // 1) Deferred actions fill at this candle's open.
        if (_pendingExit && _pos != null) ClosePosition(c.Time, c.Open, ExitReason.Crossover);
        _pendingExit = false;
        bool justOpened = false;
        if (_pendingEntry is Side side) { OpenAt(side, c); justOpened = true; }
        _pendingEntry = null;

        // 2) Manage the open position with this candle.
        if (_pos != null) ManageCandle(c, f, s, justOpened);

        // 3) New signal at this candle's close (also right after a stop-out, rule 11).
        if (warm && (_pos == null || _pendingExit))
        {
            bool up = _prevFast <= _prevSlow && f > s;
            bool down = _prevFast >= _prevSlow && f < s;
            Side? signal = up ? Side.Long : down ? Side.Short : null;
            if (signal is Side sg && _filter.Allows(sg, ctx)) _pendingEntry = sg;
        }

        _prevFast = f;
        _prevSlow = s;
    }

    /// <summary>Backtest end of data: a still-open position is closed at the given price (the last open, rule 13).</summary>
    public void CloseOpenPositionAtEndOfData(DateTime time, double price)
    {
        if (_pos != null) ClosePosition(time, price, ExitReason.EndOfData);
        _pendingEntry = null;
        _pendingExit = false;
    }

    /// <summary>
    /// Live restart helper: after replaying history to warm the EMAs, forget any simulated position / pending action so the
    /// engine starts flat. The EMA state and the previous fast/slow values are kept.
    /// </summary>
    public void ResetPositionState()
    {
        _pos = null;
        _pendingEntry = null;
        _pendingExit = false;
        Trades.Clear();
    }

    private void OpenAt(Side side, Bar c)
    {
        int d = (int)side;
        _pos = new Position
        {
            Side = side,
            EntryTime = c.Time,
            Entry = c.Open,
            InitialStop = c.Open - d * _cfg.StopPoints,
            Stop = c.Open - d * _cfg.StopPoints,
            Best = c.Open
        };
    }

    private void ManageCandle(Bar c, double f, double s, bool justOpened)
    {
        var p = _pos!;
        int d = (int)p.Side;

        // Stop first. A gap through the stop fills at the open, except on the entry candle (its open IS the entry).
        bool hit = d == 1 ? c.Low <= p.Stop : c.High >= p.Stop;
        if (hit)
        {
            bool gap = d == 1 ? c.Open < p.Stop : c.Open > p.Stop;
            double fill = (gap && !justOpened) ? c.Open : p.Stop;
            var reason = p.Stop == p.InitialStop ? ExitReason.StopLoss : ExitReason.BreakevenOrTrail;
            ClosePosition(c.Time, fill, reason);
            return;
        }

        // Breakeven / trailing: computed from this candle's high/low, effective from the NEXT candle. Ratchet only.
        p.Best = d == 1 ? Math.Max(p.Best, c.High) : Math.Min(p.Best, c.Low);
        double profit = (p.Best - p.Entry) * d;
        double r = _cfg.StopPoints;

        if (_cfg.BreakevenAtR is double be && profit >= be * r)
            p.Stop = d == 1 ? Math.Max(p.Stop, p.Entry) : Math.Min(p.Stop, p.Entry);

        if (_cfg.TrailStartR is double ts && profit >= ts * r)
        {
            double trail = p.Best - d * _cfg.TrailDistanceR * r;
            p.Stop = d == 1 ? Math.Max(p.Stop, trail) : Math.Min(p.Stop, trail);
        }

        // Crossover exit at the next open.
        bool flipped = d == 1 ? f < s : f > s;
        if (flipped) _pendingExit = true;
    }

    private void ClosePosition(DateTime time, double price, ExitReason reason)
    {
        var p = _pos!;
        double pnl = (price - p.Entry) * (int)p.Side - _cfg.CostPoints;
        Trades.Add(new Trade(p.EntryTime, time, p.Side, p.Entry, price, pnl, reason));
        _pos = null;
    }
}
