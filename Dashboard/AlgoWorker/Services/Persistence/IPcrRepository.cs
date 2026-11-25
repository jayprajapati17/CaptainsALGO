using AlgoData.Models;

namespace AlgoWorker.Services.Persistence;

/// <summary>Stores the put/call ratio snapshots the Dashboard's Live page reads.</summary>
public interface IPcrRepository
{
    Task SaveAsync(PcrSnapshotEntity snapshot, CancellationToken ct);

    /// <summary>The first snapshot of the given IST day for an index (the "day open" PCR the change is measured from); null if none yet.</summary>
    Task<PcrSnapshotEntity?> GetFirstOfDayAsync(string underlying, DateOnly day, CancellationToken ct);
}