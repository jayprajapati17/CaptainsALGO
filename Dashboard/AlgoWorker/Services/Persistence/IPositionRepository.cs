using AlgoData.Models;

namespace AlgoWorker.Services.Persistence;

/// <summary>
/// >>> NEW (Task 3): persists position lifecycle events to the shared
/// database, alongside (not instead of) the existing Telegram alerts.
/// Implementations use IDbContextFactory (not a directly-injected DbContext)
/// because the callers (VirtualPositionTracker, BreakoutPositionTracker) are
/// singletons, and EF Core's DbContext is not safe to hold long-lived inside one.
/// </summary>
public interface IPositionRepository
{
    /// <summary>Inserts a new Open position row. Returns the generated Id (used to update/close it later).</summary>
    Task<int> SaveOpenedAsync(PositionEntity position, CancellationToken ct);

    /// <summary>Updates the live premium snapshot on an open position (called on each periodic P&amp;L update).</summary>
    Task UpdateLivePremiumAsync(int positionId, decimal lastKnownPremium, DateTimeOffset lastUpdateTime, CancellationToken ct);

    /// <summary>Marks a position Closed with its final outcome.</summary>
    Task CloseAsync(
        int positionId,
        decimal exitPremium,
        DateTimeOffset exitTime,
        string exitReason,
        decimal finalPnlRupees,
        double finalPnlPercent,
        CancellationToken ct);

    /// <summary>
    /// Called once at Worker startup. The trackers hold open positions in memory only, so if the
    /// process restarted (crash, redeploy, machine reboot) any rows still marked Open in the DB
    /// are orphans -- nothing is tracking them anymore. Closes them out using their last known
    /// premium so the Dashboard's Live page doesn't show phantom positions forever. Returns how many were closed.
    /// </summary>
    Task<int> CloseOrphanedOpenPositionsAsync(string reason, CancellationToken ct);
}