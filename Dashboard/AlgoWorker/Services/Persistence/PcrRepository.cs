using AlgoData.Data;
using AlgoData.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoWorker.Services.Persistence;

public sealed class PcrRepository : IPcrRepository
{
    private readonly IDbContextFactory<NiftyBotDbContext> _dbContextFactory;
    private readonly ILogger<PcrRepository> _logger;

    public PcrRepository(IDbContextFactory<NiftyBotDbContext> dbContextFactory, ILogger<PcrRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task SaveAsync(PcrSnapshotEntity snapshot, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            db.PcrSnapshots.Add(snapshot);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to persist the PCR snapshot for {Underlying}.", snapshot.Underlying);
        }
    }

    public async Task<PcrSnapshotEntity?> GetFirstOfDayAsync(string underlying, DateOnly day, CancellationToken ct)
    {
        try
        {
            var start = MarketTime.StartOfDay(day);
            var end = MarketTime.StartOfDay(day.AddDays(1));
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            return await db.PcrSnapshots.AsNoTracking()
                .Where(p => p.Underlying == underlying && p.Time >= start && p.Time < end)
                .OrderBy(p => p.Time)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the first PCR snapshot of the day for {Underlying}.", underlying);
            return null;
        }
    }
}