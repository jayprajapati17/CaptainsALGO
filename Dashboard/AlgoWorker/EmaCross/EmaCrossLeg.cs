using AlgoWorker.EmaCross.Core;
using AlgoWorker.Models;

namespace AlgoWorker.EmaCross;

/// <summary>The paper option position behind one open EMA 9/21 trade (one leg per strategy).</summary>
public sealed class EmaCrossLeg
{
    public int Id { get; set; } = -1;                     // Positions.Id (-1 if the DB write failed)
    public required Side Side { get; init; }
    public required OptionInstrument Instrument { get; init; }
    public required int LotSize { get; init; }

    public required decimal IndexEntry { get; init; }     // Nifty spot when the leg was opened
    public decimal IndexStop { get; set; }                // current index stop-loss (moves with breakeven / trailing)
    public decimal InitialIndexStop { get; init; }

    public required decimal EntryPremium { get; init; }
    public decimal LastPremium { get; set; }
    public required DateTimeOffset OpenedAt { get; init; }
    public DateTimeOffset LastBroadcastAt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Premium safety cap level (entry less the configured %).</summary>
    public decimal PremiumStop { get; init; }

    public decimal PnlRupees => (LastPremium - EntryPremium) * LotSize;
    public double PnlPercent => EntryPremium == 0 ? 0 : (double)((LastPremium - EntryPremium) / EntryPremium) * 100.0;
    public bool StopMoved => IndexStop != InitialIndexStop;
    public bool IsCall => Instrument.Type == OptionType.Call;
}
