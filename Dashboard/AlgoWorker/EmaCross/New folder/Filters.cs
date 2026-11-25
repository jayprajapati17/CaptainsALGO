namespace AlgoWorker.EmaCross.Core;

public interface IEntryFilter
{
    bool Allows(Side side, FilterContext ctx);
}

public sealed class NoFilter : IEntryFilter
{
    public bool Allows(Side side, FilterContext ctx) => true;
}

/// <summary>Strategy A: CE only above the CPR, PE only below it. Signals inside the CPR, or on a day without a CPR, are skipped.</summary>
public sealed class CprFilter : IEntryFilter
{
    public bool Allows(Side side, FilterContext ctx)
    {
        if (ctx.Cpr is not CprRange cpr) return false;
        return side == Side.Long ? ctx.Candle.Close > cpr.Top
                                 : ctx.Candle.Close < cpr.Bottom;
    }
}

/// <summary>Strategy B (optional): the signal candle's ADX is higher than the previous candle's. NaN / unknown means false.</summary>
public sealed class AdxRisingFilter : IEntryFilter
{
    public bool Allows(Side side, FilterContext ctx)
        => ctx.Adx is double a && ctx.PrevAdx is double p && !double.IsNaN(a) && !double.IsNaN(p) && a > p;
}
