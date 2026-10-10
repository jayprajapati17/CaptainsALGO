using AlgoData.Data;
using Microsoft.EntityFrameworkCore;

namespace AlgoWorker.Services;

/// <summary>
/// Idempotent schema upgrades the Worker applies itself at startup, so nobody has to run SQL by hand.
/// Currently: add the StopLossPremium / StopLossSpot columns, and allow 'Reversal' in the Strategy CHECK constraints of Positions and SignalLog.
/// </summary>
public static class SchemaMigrator
{
    private const string AllowReversalSql = @"
IF OBJECT_ID('{TABLE}') IS NOT NULL
BEGIN
    DECLARE @ck NVARCHAR(200);
    SELECT @ck = cc.name
    FROM sys.check_constraints cc
    JOIN sys.columns col ON col.object_id = cc.parent_object_id AND col.column_id = cc.parent_column_id
    WHERE cc.parent_object_id = OBJECT_ID('{TABLE}') AND col.name = 'Strategy'
      AND cc.definition NOT LIKE '%Reversal%';

    IF @ck IS NOT NULL
    BEGIN
        EXEC('ALTER TABLE {TABLE} DROP CONSTRAINT [' + @ck + ']');
        ALTER TABLE {TABLE} ADD CHECK (Strategy IN ('Ema', 'Breakout', 'Macd', 'Reversal'));
    END
END";

    public static async Task ApplyAsync(IDbContextFactory<NiftyBotDbContext> factory, ILogger logger, CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            foreach (var table in new[] { "Positions", "SignalLog" })
                await db.Database.ExecuteSqlRawAsync(AllowReversalSql.Replace("{TABLE}", table), ct);

            // Stop-loss columns (current / trailing SL) on Positions.
            await db.Database.ExecuteSqlRawAsync(
                "IF OBJECT_ID('Positions') IS NOT NULL AND COL_LENGTH('Positions', 'StopLossPremium') IS NULL ALTER TABLE Positions ADD StopLossPremium DECIMAL(18,2) NULL", ct);
            await db.Database.ExecuteSqlRawAsync(
                "IF OBJECT_ID('Positions') IS NOT NULL AND COL_LENGTH('Positions', 'StopLossSpot') IS NULL ALTER TABLE Positions ADD StopLossSpot DECIMAL(18,2) NULL", ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Schema migration (allow 'Reversal' strategy) failed -- run the Strategy CHECK migration in schema-sqlserver.sql manually.");
        }
    }
}
