-- 数字彩用户选号票（可重复执行）
-- 与 LottoRecommendation（系统推荐）分开：用户自己买的票不参与 Replace，
-- 只追加，按期结算对奖。
IF OBJECT_ID(N'dbo.LottoUserTicket', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LottoUserTicket
    (
        TicketId      BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        LotteryCode   NVARCHAR(16)  NOT NULL,
        TargetIssue   NVARCHAR(32)  NOT NULL,
        RedNumbers    NVARCHAR(128) NOT NULL,  -- 逗号分隔，两位数字
        BlueNumbers   NVARCHAR(64)  NULL,
        PlayType      NVARCHAR(32)  NOT NULL CONSTRAINT DF_LottoUser_PlayType DEFAULT ('单式'),
        CoolScore     DECIMAL(8,2)  NULL,      -- 避热门号冷门度（手输的可为空）
        LevelName     NVARCHAR(16)  NULL,      -- 稳健 / 均衡 / 极端
        Source        NVARCHAR(8)   NOT NULL CONSTRAINT DF_LottoUser_Source DEFAULT ('pick'),  -- pick=选号器 / manual=手输
        BetAmount     DECIMAL(8,2)  NOT NULL CONSTRAINT DF_LottoUser_BetAmount DEFAULT (2),
        RecDate       DATE          NOT NULL,
        CreatedAt     DATETIME2(0)  NOT NULL CONSTRAINT DF_LottoUser_CreatedAt DEFAULT (SYSDATETIME())
    );
    CREATE INDEX IX_LottoUser_CodeDate ON dbo.LottoUserTicket (LotteryCode, RecDate DESC);
END
GO
