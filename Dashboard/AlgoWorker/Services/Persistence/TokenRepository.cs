using AlgoData.Data;
using AlgoData.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoWorker.Services.Persistence;

public sealed class TokenRepository : ITokenRepository
{
    private readonly IDbContextFactory<NiftyBotDbContext> _dbContextFactory;
    private readonly IClock _clock;
    private readonly ILogger<TokenRepository> _logger;

    public TokenRepository(IDbContextFactory<NiftyBotDbContext> dbContextFactory, IClock clock, ILogger<TokenRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _clock = clock;
        _logger = logger;
    }

    public async Task<AccessTokenEntity?> GetTodaysTokenEntityAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            var today = _clock.Today;
            return await db.AccessTokens.FirstOrDefaultAsync(t => t.TokenDate == today, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read today's access token from the database.");
            return null; // caller (UpstoxRestClient) falls back to appsettings.json's AccessToken
        }
    }

    public async Task SaveTodaysTokenAsync(string token, CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var today = _clock.Today;

        var existing = await db.AccessTokens.FirstOrDefaultAsync(t => t.TokenDate == today, ct);
        if (existing is null)
        {
            db.AccessTokens.Add(new AccessTokenEntity
            {
                TokenDate = today,
                Token = token,
                GeneratedAt = _clock.Now
            });
        }
        else
        {
            existing.Token = token;
            existing.GeneratedAt = _clock.Now;
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Access token saved for {Date}.", today);
    }
}