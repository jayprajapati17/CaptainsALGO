using AlgoData.Data;
using AlgoData.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoWorker.Services.Persistence;

public sealed class PositionRepository : IPositionRepository
{
    private readonly IDbContextFactory<NiftyBotDbContext> _dbContextFactory;
    private readonly ILogger<PositionRepository> _logger;

    public PositionRepository(IDbContextFactory<NiftyBotDbContext> dbContextFactory, ILogger<PositionRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<int> SaveOpenedAsync(PositionEntity position, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            db.Positions.Add(position);
            await db.SaveChangesAsync(ct);
            return position.Id;
        }
        catch (Exception ex)
        {
            // >>> Deliberately non-fatal: a DB write failure should never take
            // down the live trading pipeline or block a Telegram alert. Log
            // and move on -- the position still exists in memory and still
            // gets tracked/alerted normally, it just won't show up on the
            // dashboard until the next successful write.
            _logger.LogError(ex, "Failed to persist newly-opened position ({Symbol}) to the database.", position.TradingSymbol);
            return -1;
        }
    }

    public async Task UpdateLivePremiumAsync(int positionId, decimal lastKnownPremium, DateTimeOffset lastUpdateTime, CancellationToken ct)
    {
        if (positionId < 0) return; // the initial insert failed -- nothing to update

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            var rows = await db.Positions
                .Where(p => p.Id == positionId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.LastKnownPremium, lastKnownPremium)
                    .SetProperty(p => p.LastUpdateTime, lastUpdateTime), ct);

            if (rows == 0)
                _logger.LogWarning("UpdateLivePremiumAsync: no row matched PositionId {Id}.", positionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update live premium for PositionId {Id}.", positionId);
        }
    }

    public async Task CloseAsync(
        int positionId, decimal exitPremium, DateTimeOffset exitTime, string exitReason,
        decimal finalPnlRupees, double finalPnlPercent, CancellationToken ct)
    {
        if (positionId < 0) return;

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            var rows = await db.Positions
                .Where(p => p.Id == positionId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.Status, PositionStatus.Closed)
                    .SetProperty(p => p.ExitPremium, exitPremium)
                    .SetProperty(p => p.ExitTime, exitTime)
                    .SetProperty(p => p.ExitReason, exitReason)
                    .SetProperty(p => p.FinalPnlRupees, finalPnlRupees)
                    .SetProperty(p => p.FinalPnlPercent, finalPnlPercent)
                    .SetProperty(p => p.LastKnownPremium, exitPremium)
                    .SetProperty(p => p.LastUpdateTime, exitTime), ct);

            if (rows == 0)
                _logger.LogWarning("CloseAsync: no row matched PositionId {Id}.", positionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark PositionId {Id} as closed.", positionId);
        }
    }

    public async Task<int> CloseOrphanedOpenPositionsAsync(string reason, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            var orphans = await db.Positions.Where(p => p.Status == PositionStatus.Open).ToListAsync(ct);
            if (orphans.Count == 0)
                return 0;

            var now = DateTimeOffset.Now;
            foreach (var p in orphans)
            {
                // Best available outcome: last premium we saw before the process went away.
                p.Status = PositionStatus.Closed;
                p.ExitPremium = p.LastKnownPremium;
                p.ExitTime = now;
                p.ExitReason = reason;
                p.FinalPnlRupees = (p.LastKnownPremium - p.EntryPremium) * p.LotSize;
                p.FinalPnlPercent = p.EntryPremium == 0
                    ? 0
                    : (double)((p.LastKnownPremium - p.EntryPremium) / p.EntryPremium) * 100.0;
                p.LastUpdateTime = now;
            }

            await db.SaveChangesAsync(ct);
            _logger.LogWarning("Closed {Count} orphaned Open position(s) left over from a previous Worker run.", orphans.Count);
            return orphans.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to close orphaned Open positions at startup.");
            return 0;
        }
    }
}