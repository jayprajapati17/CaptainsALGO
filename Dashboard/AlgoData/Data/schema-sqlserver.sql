-- =====================================================================
-- Nifty Bot Dashboard -- Database Schema (SQL SERVER / T-SQL version)
-- Shared by NiftyEmaAlertBot (Worker Service, writes) and
-- NiftyBot.Dashboard (MVC app, reads + issues close/token commands).
--
-- Idempotent: safe to run this script multiple times against the same
-- database (every CREATE is guarded with an existence check, T-SQL style --
-- SQL Server doesn't support "CREATE TABLE IF NOT EXISTS" directly).
--
-- NOTE: the target DATABASE (e.g. "NiftyBot") must already exist on the
-- server -- this script does not run CREATE DATABASE.
-- =====================================================================

-- ---------------------------------------------------------------------
-- Positions
-- ---------------------------------------------------------------------
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Positions')
BEGIN
    CREATE TABLE Positions (
        Id                  INT IDENTITY(1,1) PRIMARY KEY,
        Strategy            NVARCHAR(20)    NOT NULL CHECK (Strategy IN ('Ema', 'Breakout', 'Macd', 'Reversal')),
        Direction           NVARCHAR(30)    NOT NULL,             -- 'GoldenCross' | 'DeathCross' | 'Up' | 'Down'
        Leg                 NVARCHAR(20)    NULL,                  -- 'CurrentWeekItm' | 'NextWeekAtm' | NULL for Breakout
        InstrumentKey       NVARCHAR(100)   NOT NULL,
        TradingSymbol       NVARCHAR(50)    NOT NULL,              -- e.g. "NIFTY50 24350CE"
        Strike              INT             NOT NULL,
        OptionType          NVARCHAR(2)     NOT NULL CHECK (OptionType IN ('CE', 'PE')),
        Expiry              DATE            NOT NULL,
        LotSize             INT             NOT NULL,

        SpotAtEntry         DECIMAL(18,2)   NOT NULL,
        EntryPremium        DECIMAL(18,2)   NOT NULL,
        EntryTime           DATETIMEOFFSET  NOT NULL,

        Confidence          NVARCHAR(10)    NULL CHECK (Confidence IN ('High', 'Medium', 'Low')),
        AdxAtEntry          FLOAT           NULL,
        AdxNCandlesAgo      FLOAT           NULL,
        BrokenLevel         DECIMAL(18,2)   NULL,

        Status              NVARCHAR(10)    NOT NULL CONSTRAINT DF_Positions_Status DEFAULT ('Open')
                                             CHECK (Status IN ('Open', 'Closed')),
        LastKnownPremium    DECIMAL(18,2)   NOT NULL,
        LastUpdateTime      DATETIMEOFFSET  NOT NULL,

        StopLossPremium     DECIMAL(18,2)   NULL,
        StopLossSpot        DECIMAL(18,2)   NULL,

        ExitPremium         DECIMAL(18,2)   NULL,
        ExitTime            DATETIMEOFFSET  NULL,
        ExitReason          NVARCHAR(200)   NULL,
        FinalPnlRupees      DECIMAL(18,2)   NULL,
        FinalPnlPercent     FLOAT           NULL,

        CreatedAt           DATETIMEOFFSET  NOT NULL CONSTRAINT DF_Positions_CreatedAt DEFAULT (SYSDATETIMEOFFSET())
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Positions_Status' AND object_id = OBJECT_ID('Positions'))
    CREATE INDEX IX_Positions_Status ON Positions (Status);
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Positions_EntryTime' AND object_id = OBJECT_ID('Positions'))
    CREATE INDEX IX_Positions_EntryTime ON Positions (EntryTime);
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Positions_Strategy' AND object_id = OBJECT_ID('Positions'))
    CREATE INDEX IX_Positions_Strategy ON Positions (Strategy);
GO

-- ---------------------------------------------------------------------
-- SignalLog
-- ---------------------------------------------------------------------
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SignalLog')
BEGIN
    CREATE TABLE SignalLog (
        Id                  INT IDENTITY(1,1) PRIMARY KEY,
        Strategy            NVARCHAR(20)    NOT NULL CHECK (Strategy IN ('Ema', 'Breakout', 'Macd', 'Reversal')),
        Direction           NVARCHAR(30)    NOT NULL,
        Confidence          NVARCHAR(10)    NULL CHECK (Confidence IN ('High', 'Medium', 'Low')),
        SignalTime          DATETIMEOFFSET  NOT NULL,
        SpotPrice           DECIMAL(18,2)   NOT NULL,
        AdxAtSignal         FLOAT           NULL,
        AdxNCandlesAgo      FLOAT           NULL,
        BrokenLevel         DECIMAL(18,2)   NULL,
        IsCatchUp           BIT             NOT NULL CONSTRAINT DF_SignalLog_IsCatchUp DEFAULT (0),

        PositionOpened      BIT             NOT NULL CONSTRAINT DF_SignalLog_PositionOpened DEFAULT (0),
        PositionId          INT             NULL CONSTRAINT FK_SignalLog_Positions REFERENCES Positions(Id),
        SkipReason          NVARCHAR(200)   NULL,

        CreatedAt           DATETIMEOFFSET  NOT NULL CONSTRAINT DF_SignalLog_CreatedAt DEFAULT (SYSDATETIMEOFFSET())
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SignalLog_SignalTime' AND object_id = OBJECT_ID('SignalLog'))
    CREATE INDEX IX_SignalLog_SignalTime ON SignalLog (SignalTime);
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_SignalLog_Strategy' AND object_id = OBJECT_ID('SignalLog'))
    CREATE INDEX IX_SignalLog_Strategy ON SignalLog (Strategy);
GO

-- ---------------------------------------------------------------------
-- DailyCpr
-- ---------------------------------------------------------------------
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'DailyCpr')
BEGIN
    CREATE TABLE DailyCpr (
        Id                  INT IDENTITY(1,1) PRIMARY KEY,
        ForTradingDay       DATE            NOT NULL,
        Label               NVARCHAR(15)    NOT NULL CHECK (Label IN ('TodaysCpr', 'NextDayCpr')),

        SourceHigh          DECIMAL(18,2)   NOT NULL,
        SourceLow           DECIMAL(18,2)   NOT NULL,
        SourceClose         DECIMAL(18,2)   NOT NULL,

        CPRPivot               DECIMAL(18,2)   NOT NULL,
        Tc                  DECIMAL(18,2)   NOT NULL,
        Bc                  DECIMAL(18,2)   NOT NULL,
        R1                  DECIMAL(18,2)   NOT NULL,
        S1                  DECIMAL(18,2)   NOT NULL,
        R2                  DECIMAL(18,2)   NOT NULL,
        S2                  DECIMAL(18,2)   NOT NULL,

        WidthPercent        FLOAT           NOT NULL,
        Reading             NVARCHAR(200)   NOT NULL,
        BiasNote            NVARCHAR(200)   NOT NULL,

        ComputedAt          DATETIMEOFFSET  NOT NULL CONSTRAINT DF_DailyCpr_ComputedAt DEFAULT (SYSDATETIMEOFFSET()),

        CONSTRAINT UQ_DailyCpr_Day_Label UNIQUE (ForTradingDay, Label)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_DailyCpr_ForTradingDay' AND object_id = OBJECT_ID('DailyCpr'))
    CREATE INDEX IX_DailyCpr_ForTradingDay ON DailyCpr (ForTradingDay);
GO

-- ---------------------------------------------------------------------
-- AccessTokens -- one row per day; UNIQUE constraint drives the
-- dashboard's "Generate token" button vs. label state.
-- ---------------------------------------------------------------------
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AccessTokens')
BEGIN
    CREATE TABLE AccessTokens (
        Id                  INT IDENTITY(1,1) PRIMARY KEY,
        TokenDate           DATE            NOT NULL,
        Token               NVARCHAR(500)   NOT NULL,
        GeneratedAt         DATETIMEOFFSET  NOT NULL CONSTRAINT DF_AccessTokens_GeneratedAt DEFAULT (SYSDATETIMEOFFSET()),

        CONSTRAINT UQ_AccessTokens_TokenDate UNIQUE (TokenDate)
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_AccessTokens_TokenDate' AND object_id = OBJECT_ID('AccessTokens'))
    CREATE INDEX IX_AccessTokens_TokenDate ON AccessTokens (TokenDate);
GO

-- =====================================================================
-- Convenience views
-- NOTE: CREATE VIEW must be the only statement in its batch in T-SQL, so
-- each one is wrapped in dynamic SQL (EXEC) to keep it inside the IF check.
-- =====================================================================

IF NOT EXISTS (SELECT * FROM sys.views WHERE name = 'vw_OpenPositions')
    EXEC('CREATE VIEW vw_OpenPositions AS SELECT * FROM Positions WHERE Status = ''Open''');
GO

IF NOT EXISTS (SELECT * FROM sys.views WHERE name = 'vw_ClosedPositions')
    EXEC('
        CREATE VIEW vw_ClosedPositions AS
        SELECT *, DATEDIFF(MINUTE, EntryTime, ExitTime) AS DurationMinutes
        FROM Positions
        WHERE Status = ''Closed''
    ');
GO

IF NOT EXISTS (SELECT * FROM sys.views WHERE name = 'vw_HistorySummary')
    EXEC('
        CREATE VIEW vw_HistorySummary AS
        SELECT
            COUNT(*) AS TotalTrades,
            SUM(CASE WHEN FinalPnlRupees > 0 THEN 1 ELSE 0 END) AS WinningTrades,
            ROUND(100.0 * SUM(CASE WHEN FinalPnlRupees > 0 THEN 1 ELSE 0 END) / NULLIF(COUNT(*), 0), 1) AS WinRatePercent,
            SUM(FinalPnlRupees) AS TotalPnlRupees
        FROM Positions
        WHERE Status = ''Closed''
    ');
GO
-- ---------------------------------------------------------------------
-- Migration: allow 'Macd' in the Strategy CHECK constraints (Positions,
-- SignalLog). Needed for databases created BEFORE the MACD strategy
-- existed -- the CREATE TABLE guards above are IF-NOT-EXISTS'd and won't
-- retroactively touch an already-created table, so this runs separately.
-- Idempotent: only acts if the existing constraint doesn't already allow 'Macd'.
-- ---------------------------------------------------------------------
IF OBJECT_ID('Positions') IS NOT NULL
BEGIN
    DECLARE @posStrategyCk NVARCHAR(200);
    SELECT @posStrategyCk = cc.name
    FROM sys.check_constraints cc
    JOIN sys.columns col ON col.object_id = cc.parent_object_id AND col.column_id = cc.parent_column_id
    WHERE cc.parent_object_id = OBJECT_ID('Positions') AND col.name = 'Strategy'
      AND cc.definition NOT LIKE '%Reversal%';

    IF @posStrategyCk IS NOT NULL
    BEGIN
        EXEC('ALTER TABLE Positions DROP CONSTRAINT [' + @posStrategyCk + ']');
        ALTER TABLE Positions ADD CHECK (Strategy IN ('Ema', 'Breakout', 'Macd', 'Reversal'));
    END
END
GO

IF OBJECT_ID('SignalLog') IS NOT NULL
BEGIN
    DECLARE @sigStrategyCk NVARCHAR(200);
    SELECT @sigStrategyCk = cc.name
    FROM sys.check_constraints cc
    JOIN sys.columns col ON col.object_id = cc.parent_object_id AND col.column_id = cc.parent_column_id
    WHERE cc.parent_object_id = OBJECT_ID('SignalLog') AND col.name = 'Strategy'
      AND cc.definition NOT LIKE '%Reversal%';

    IF @sigStrategyCk IS NOT NULL
    BEGIN
        EXEC('ALTER TABLE SignalLog DROP CONSTRAINT [' + @sigStrategyCk + ']');
        ALTER TABLE SignalLog ADD CHECK (Strategy IN ('Ema', 'Breakout', 'Macd', 'Reversal'));
    END
END
GO

-- ---------------------------------------------------------------------
-- AppSettings -- the Worker's Upstox / Telegram / Strategy settings,
-- editable from the Dashboard's Settings page. The Worker creates this
-- table itself and seeds it from appsettings.json on its first start, so
-- running this block manually is optional (it is idempotent either way).
-- ---------------------------------------------------------------------
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AppSettings')
BEGIN
    CREATE TABLE AppSettings (
        Id               INT IDENTITY(1,1) PRIMARY KEY,
        Section          NVARCHAR(100)   NOT NULL,
        [Key]            NVARCHAR(150)   NOT NULL,
        [Value]          NVARCHAR(MAX)   NULL,
        ValueType        NVARCHAR(20)    NOT NULL CONSTRAINT DF_AppSettings_ValueType DEFAULT ('string'),
        IsSecret         BIT             NOT NULL CONSTRAINT DF_AppSettings_IsSecret DEFAULT (0),
        RequiresRestart  BIT             NOT NULL CONSTRAINT DF_AppSettings_RequiresRestart DEFAULT (0),
        GroupName        NVARCHAR(100)   NOT NULL CONSTRAINT DF_AppSettings_GroupName DEFAULT (''),
        SortOrder        INT             NOT NULL CONSTRAINT DF_AppSettings_SortOrder DEFAULT (0),
        [Description]    NVARCHAR(500)   NOT NULL CONSTRAINT DF_AppSettings_Description DEFAULT (''),
        UpdatedAt        DATETIMEOFFSET  NOT NULL CONSTRAINT DF_AppSettings_UpdatedAt DEFAULT (SYSDATETIMEOFFSET()),

        CONSTRAINT UQ_AppSettings_Section_Key UNIQUE (Section, [Key])
    );
END
GO

-- ---------------------------------------------------------------------
-- Migration: stop-loss columns on Positions (current/trailing SL; the Worker also adds
-- these itself at startup). Idempotent.
-- ---------------------------------------------------------------------
IF OBJECT_ID('Positions') IS NOT NULL AND COL_LENGTH('Positions', 'StopLossPremium') IS NULL
    ALTER TABLE Positions ADD StopLossPremium DECIMAL(18,2) NULL;
GO
IF OBJECT_ID('Positions') IS NOT NULL AND COL_LENGTH('Positions', 'StopLossSpot') IS NULL
    ALTER TABLE Positions ADD StopLossSpot DECIMAL(18,2) NULL;
GO
