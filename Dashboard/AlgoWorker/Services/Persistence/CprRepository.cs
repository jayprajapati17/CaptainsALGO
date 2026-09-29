using AlgoData.Data;
using AlgoData.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoWorker.Services.Persistence;

public sealed class CprRepository : ICprRepository
{
    private readonly IDbContextFactory<NiftyBotDbContext> _dbContextFactory;
    private readonly ILogger<CprRepository> _logger;

    public CprRepository(IDbContextFactory<NiftyBotDbContext> dbContextFactory, ILogger<CprRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task SaveAsync(DailyCprEntity entry, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

            // Upsert on (ForTradingDay, Label) -- matches the UQ_DailyCpr_Day_Label
            // constraint. A day's CPR could legitimately be recomputed (e.g. if
            // the bot restarts), so overwrite rather than fail on the unique constraint.
            var existing = await db.DailyCprs.FirstOrDefaultAsync(
                c => c.ForTradingDay == entry.ForTradingDay && c.Label == entry.Label, ct);

            if (existing is null)
            {
                db.DailyCprs.Add(entry);
            }
            else
            {
                existing.SourceHigh = entry.SourceHigh;
                existing.SourceLow = entry.SourceLow;
                existing.SourceClose = entry.SourceClose;
                existing.CPRPivot = entry.CPRPivot;
                existing.Tc = entry.Tc;
                existing.Bc = entry.Bc;
                existing.R1 = entry.R1;
                existing.S1 = entry.S1;
                existing.R2 = entry.R2;
                existing.S2 = entry.S2;
                existing.WidthPercent = entry.WidthPercent;
                existing.Reading = entry.Reading;
                existing.BiasNote = entry.BiasNote;
                existing.ComputedAt = entry.ComputedAt;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist DailyCpr entry for {Day} ({Label}).", entry.ForTradingDay, entry.Label);
        }
    }
}