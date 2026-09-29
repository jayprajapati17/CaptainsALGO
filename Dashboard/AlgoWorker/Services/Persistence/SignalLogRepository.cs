using AlgoData.Data;
using AlgoData.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoWorker.Services.Persistence;

public sealed class SignalLogRepository : ISignalLogRepository
{
    private readonly IDbContextFactory<NiftyBotDbContext> _dbContextFactory;
    private readonly ILogger<SignalLogRepository> _logger;

    public SignalLogRepository(IDbContextFactory<NiftyBotDbContext> dbContextFactory, ILogger<SignalLogRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task LogAsync(SignalLogEntity entry, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            db.SignalLogs.Add(entry);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Non-fatal, same reasoning as PositionRepository -- never let a
            // DB hiccup interrupt live trading logic or Telegram alerts.
            _logger.LogError(ex, "Failed to persist signal log entry ({Strategy} {Direction}).", entry.Strategy, entry.Direction);
        }
    }
}