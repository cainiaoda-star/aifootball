/* 北单历史开奖：每期每场的让球数、平均欧赔、比分和开出选项的 SP。 */
IF OBJECT_ID(N'dbo.FbBdHistory', N'U') IS NULL
CREATE TABLE dbo.FbBdHistory
(
    Expect      INT           NOT NULL,
    MatchNo     INT           NOT NULL,
    MatchId     BIGINT        NULL,
    LeagueName  NVARCHAR(50)  NULL,
    EndTime     DATETIME2(0)  NULL,
    HomeTeam    NVARCHAR(60)  NULL,
    AwayTeam    NVARCHAR(60)  NULL,
    Handicap    DECIMAL(4,2)  NULL,
    AvgHome     DECIMAL(8,2)  NULL,
    AvgDraw     DECIMAL(8,2)  NULL,
    AvgAway     DECIMAL(8,2)  NULL,
    AsianHand   NVARCHAR(20)  NULL,
    HomeScore   TINYINT       NULL,
    AwayScore   TINYINT       NULL,
    ResultCode  CHAR(1)       NULL,
    ResultSp    DECIMAL(10,2) NULL,
    CapturedAt  DATETIME2(0)  NOT NULL CONSTRAINT DF_FbBdHistory_CapturedAt DEFAULT SYSDATETIME(),
    CONSTRAINT PK_FbBdHistory PRIMARY KEY (Expect, MatchNo)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FbBdHistory_MatchId' AND object_id = OBJECT_ID(N'dbo.FbBdHistory'))
    CREATE INDEX IX_FbBdHistory_MatchId ON dbo.FbBdHistory (MatchId);
GO
