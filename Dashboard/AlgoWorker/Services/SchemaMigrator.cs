using AlgoData.Data;
using Microsoft.EntityFrameworkCore;

namespace AlgoWorker.Services;

/// <summary>
/// Idempotent schema upgrades the Worker applies itself at startup, so nobody has to run SQL by hand.
/// Currently: add the StopLossPremium / StopLossSpot columns, and allow 'Reversal', 'EmaCrossA' and 'EmaCrossB' in the Strategy CHECK constraints of Positions and SignalLog.
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
      AND cc.definition NOT LIKE '%EmaCrossB%';

    IF @ck IS NOT NULL
    BEGIN
        EXEC('ALTER TABLE {TABLE} DROP CONSTRAINT [' + @ck + ']');
        ALTER TABLE {TABLE} ADD CHECK (Strategy IN ('Ema', 'Breakout', 'Macd', 'Reversal', 'EmaCrossA', 'EmaCrossB'));
    END
END";

    private const string DailyCprUnderlyingSql = @"
IF OBJECT_ID('DailyCpr') IS NOT NULL
BEGIN
    IF COL_LENGTH('DailyCpr', 'Underlying') IS NULL
        EXEC('ALTER TABLE DailyCpr ADD Underlying NVARCHAR(20) NOT NULL CONSTRAINT DF_DailyCpr_Underlying DEFAULT ''NIFTY50''');

    IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_DailyCpr_Day_Label' AND parent_object_id = OBJECT_ID('DailyCpr'))
        EXEC('ALTER TABLE DailyCpr DROP CONSTRAINT UQ_DailyCpr_Day_Label');

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_DailyCpr_Day_Label_Underlying' AND object_id = OBJECT_ID('DailyCpr'))
        EXEC('CREATE UNIQUE INDEX UQ_DailyCpr_Day_Label_Underlying ON DailyCpr (ForTradingDay, Label, Underlying)');
END";

    private const string PcrSnapshotSql = @"
IF OBJECT_ID('PcrSnapshot') IS NULL
BEGIN
    CREATE TABLE PcrSnapshot (
        Id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
        Underlying          NVARCHAR(20)    NOT NULL,
        [Time]              DATETIMEOFFSET  NOT NULL,
        Expiry              DATE            NOT NULL,
        Spot                DECIMAL(18,2)   NOT NULL,
        FullPcr             FLOAT           NULL,
        WindowPcr           FLOAT           NULL,
        TotalCallOi         DECIMAL(18,0)   NOT NULL,
        TotalPutOi          DECIMAL(18,0)   NOT NULL,
        SupportStrike       DECIMAL(18,2)   NULL,
        ResistanceStrike    DECIMAL(18,2)   NULL,
        Label               NVARCHAR(20)    NOT NULL
    );
END
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PcrSnapshot_Underlying_Time' AND object_id = OBJECT_ID('PcrSnapshot'))
    EXEC('CREATE INDEX IX_PcrSnapshot_Underlying_Time ON PcrSnapshot (Underlying, [Time])');";

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

            // DailyCpr: one row per index per day (Nifty + Bank Nifty). Existing rows become NIFTY50; the old (Day, Label) unique key is replaced.
            await db.Database.ExecuteSqlRawAsync(DailyCprUnderlyingSql, ct);

            // PcrSnapshot: put/call ratio history (created on first start after the PCR update).
            await db.Database.ExecuteSqlRawAsync(PcrSnapshotSql, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Schema migration (allow 'Reversal' strategy) failed -- run the Strategy CHECK migration in schema-sqlserver.sql manually.");
        }
    }
}