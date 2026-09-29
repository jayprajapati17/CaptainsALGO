using AlgoData.Models;

namespace AlgoWorker.Services.Persistence;

/// <summary>>>> NEW (Task 3): persists both daily CPR alerts (morning "Today's CPR" and evening "Next-Day CPR") to the shared database.</summary>
public interface ICprRepository
{
    /// <summary>Upserts by (ForTradingDay, Label) -- matches the UNIQUE constraint, safe to call again if the same day/label recomputes.</summary>
    Task SaveAsync(DailyCprEntity entry, CancellationToken ct);
}