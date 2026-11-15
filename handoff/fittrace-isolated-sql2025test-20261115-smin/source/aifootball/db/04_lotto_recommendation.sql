-- 数字彩选号方案（可重复执行）
IF OBJECT_ID(N'dbo.LottoRecommendation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LottoRecommendation
    (
        RecId         BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        LotteryCode   NVARCHAR(16)  NOT NULL,
        TargetIssue   NVARCHAR(32)  NOT NULL,
        BasedOnIssue  NVARCHAR(32)  NULL,
        RankNo        INT           NOT NULL,
        IsPrimary     BIT           NOT NULL CONSTRAINT DF_LottoRec_IsPrimary DEFAULT (0),
        PlayType      NVARCHAR(32)  NOT NULL,
        RedNumbers    NVARCHAR(64)  NOT NULL,
        BlueNumbers   NVARCHAR(32)  NULL,
        Score         DECIMAL(8,2)  NULL,
        SignalDesc    NVARCHAR(500) NULL,
        RecDate       DATE          NOT NULL,
        CreatedAt     DATETIME2(0)  NOT NULL CONSTRAINT DF_LottoRec_CreatedAt DEFAULT (SYSDATETIME()),
        CONSTRAINT UQ_LottoRec_CodeIssueRank UNIQUE (LotteryCode, TargetIssue, RankNo)
    );
    CREATE INDEX IX_LottoRec_CodeDate ON dbo.LottoRecommendation (LotteryCode, RecDate DESC);
END
GO
