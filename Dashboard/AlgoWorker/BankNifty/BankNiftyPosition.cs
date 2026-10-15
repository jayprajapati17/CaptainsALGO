using AlgoWorker.Models;

namespace AlgoWorker.BankNifty;

/// <summary>In-memory state of the one open virtual Bank Nifty position.</summary>
public sealed class BankNiftyPosition
{
    public int Id { get; set; } = -1;
    public required BankNiftySignal Signal { get; init; }
    public required OptionInstrument Instrument { get; init; }
    public int LotSize { get; init; }

    public decimal EntryPremium { get; init; }
    public DateTimeOffset OpenedAt { get; init; }

    public decimal LastKnownPremium { get; set; }
    public decimal PeakPremium { get; set; }
    public decimal CurrentStopLossPremium { get; set; }
    public bool TrailingActive { get; set; }
    public bool IsOpen { get; set; } = true;

    public DateTimeOffset LastUpdateSentAt { get; set; }
    public DateTimeOffset LastBroadcastAt { get; set; } = DateTimeOffset.MinValue;

    public decimal PnlRupees => (LastKnownPremium - EntryPremium) * LotSize;
    public decimal PnlPercent => EntryPremium == 0 ? 0 : (LastKnownPremium - EntryPremium) / EntryPremium * 100m;
}
