/* ============================================================
   购买记录（自购方案）主从表
   主表 Purchase：一次投注一个方案（过关串关）
   从表 PurchaseLine：方案内每一场的投注明细
   幂等：IF OBJECT_ID 判断
   ============================================================ */

/* 主表：购买方案 */
IF OBJECT_ID(N'dbo.Purchase', N'U') IS NULL
CREATE TABLE dbo.Purchase
(
    PurchaseId      BIGINT IDENTITY(1,1) PRIMARY KEY,
    Board           VARCHAR(10)   NOT NULL,            -- jc=竞彩 / bd=北单 / sfc=胜负彩
    Expect          NVARCHAR(20)  NULL,                -- 期号/方案号
    PlayType        NVARCHAR(50)  NULL,                -- 玩法：胜平负 / 让球胜平负 / 全场比分 / 总进球 等
    PassType        NVARCHAR(50)  NULL,                -- 过关方式：单关 / 2串1 / 3串1#4串1 等
    Multiple        INT           NOT NULL CONSTRAINT DF_Purchase_Multiple DEFAULT 1,  -- 倍数
    BetCount        INT           NULL,                -- 总注数
    BetAmount       DECIMAL(10,2) NOT NULL CONSTRAINT DF_Purchase_BetAmount DEFAULT 0, -- 投注金额(元)
    ExpectedWin     DECIMAL(12,2) NULL,                -- 预计中奖金额(元)
    ActualWin       DECIMAL(12,2) NULL,                -- 实际中奖金额(元，开奖后)
    Status          VARCHAR(20)   NOT NULL CONSTRAINT DF_Purchase_Status DEFAULT 'PENDING', -- PENDING待出票 / WAIT待开奖 / WON中奖 / LOST未中 / CANCELLED撤销
    BuyDate         DATE          NOT NULL CONSTRAINT DF_Purchase_BuyDate DEFAULT CAST(SYSDATETIME() AS DATE), -- 购买日期
    TicketImage     NVARCHAR(500) NULL,                -- 购买截图本地路径
    ResultImage     NVARCHAR(500) NULL,                -- 开奖截图本地路径
    IsHit           BIT           NULL,                -- 是否中奖（整体）
    HitLevel        NVARCHAR(50)  NULL,                -- 命中奖级/串关描述
    Remark          NVARCHAR(500) NULL,                -- 备注 / 复盘分析
    CreatedAt       DATETIME2(0)  NOT NULL CONSTRAINT DF_Purchase_CreatedAt DEFAULT SYSDATETIME(),
    UpdatedAt       DATETIME2(0)  NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Purchase_BuyDate' AND object_id = OBJECT_ID(N'dbo.Purchase'))
    CREATE INDEX IX_Purchase_BuyDate ON dbo.Purchase (BuyDate DESC);
GO

/* 从表：方案内场次明细 */
IF OBJECT_ID(N'dbo.PurchaseLine', N'U') IS NULL
CREATE TABLE dbo.PurchaseLine
(
    LineId          BIGINT IDENTITY(1,1) PRIMARY KEY,
    PurchaseId      BIGINT        NOT NULL,
    Seq             INT           NOT NULL,            -- 场次序号
    MatchNo         NVARCHAR(20)  NULL,                -- 场次号（如 周三001）
    MatchId         BIGINT        NULL,                -- 关联 500.com fid（可空）
    LeagueName      NVARCHAR(100) NULL,
    HomeTeam        NVARCHAR(100) NULL,
    AwayTeam        NVARCHAR(100) NULL,
    MatchTime       DATETIME2(0)  NULL,
    Handicap        DECIMAL(6,2)  NULL,                -- 让球（主让为负）
    Option1         NVARCHAR(50)  NULL,                -- 投注选项1（如 比分 2:0 / 主胜）
    Option2         NVARCHAR(50)  NULL,                -- 投注选项2（如 3:1）
    Odds1           DECIMAL(8,3)  NULL,                -- 选项1赔率/水位
    Odds2           DECIMAL(8,3)  NULL,                -- 选项2赔率/水位
    ActualScore     NVARCHAR(10)  NULL,                -- 实际比分（开奖后回填）
    ActualGoals     INT           NULL,                -- 实际总进球
    ActualResult    CHAR(1)       NULL,                -- 实际胜平负 3/1/0
    LineHit         BIT           NULL,                -- 该场选项是否命中
    CONSTRAINT FK_PurchaseLine_Purchase FOREIGN KEY (PurchaseId) REFERENCES dbo.Purchase (PurchaseId) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PurchaseLine_Purchase' AND object_id = OBJECT_ID(N'dbo.PurchaseLine'))
    CREATE INDEX IX_PurchaseLine_Purchase ON dbo.PurchaseLine (PurchaseId, Seq);
GO
