/* ============================================================
   票面台账 —— 按票型统计中奖率 / 回本率的唯一数据来源
   ------------------------------------------------------------
   在此之前，票只在 /api/recommend 请求时临时算出来给人看，从未落库。
   于是「8 腿票这个月发了几张、中了几张、回本几张」系统答不了，
   只能靠概率口算。这张表把每张实际发出去的票记下来，赛后结算。

   两张表：
     TicketPlan     一期一票（同一票型一天一张，重复落库按唯一键覆盖）
     TicketPlanLine 票内每腿的选择、水位、赛果

   结算复用 PurchaseSettleMath.CalculatePrize，与手工购买的票
   走同一套注数展开与扣税规则，不会出现「系统算的奖」和
   「实际兑奖」两套口径。
   ============================================================ */

IF OBJECT_ID(N'dbo.TicketPlan', N'U') IS NULL
CREATE TABLE dbo.TicketPlan
(
    TicketId        BIGINT IDENTITY(1,1) PRIMARY KEY,
    PlanDate        DATE          NOT NULL,                -- 出票日
    Board           VARCHAR(10)   NOT NULL,                -- jc=竞彩 / bd=北单
    PassType        NVARCHAR(50)  NOT NULL,                -- 单关 / 2串1 / 6串1#7串1#8串1
    LegCount        INT           NOT NULL,                -- 腿数
    BetCount        INT           NOT NULL,                -- 注数
    Multiple        INT           NOT NULL CONSTRAINT DF_TicketPlan_Multiple DEFAULT 1,
    BetAmount       DECIMAL(10,2) NOT NULL,                -- 本金
    HitProbability  DECIMAL(6,2)  NULL,                    -- 票面中奖率（模型，%）
    BackProbability DECIMAL(6,2)  NULL,                    -- 票面回本率（模型，%）
    ExpectedReturn  DECIMAL(8,4)  NULL,                    -- 期望回报（元/元）
    Status          VARCHAR(20)   NOT NULL CONSTRAINT DF_TicketPlan_Status DEFAULT 'PENDING',
                                                           -- PENDING / WON(有奖) / LOST(无奖) / VOID(作废)
    HitLegCount     INT           NULL,                    -- 命中腿数
    PrizeAmount     DECIMAL(12,2) NULL,                    -- 实际奖金
    SettledAt       DATETIME2(0)  NULL,
    CreatedAt       DATETIME2(0)  NOT NULL CONSTRAINT DF_TicketPlan_CreatedAt DEFAULT SYSDATETIME(),
    CONSTRAINT UQ_TicketPlan UNIQUE (PlanDate, Board, PassType)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TicketPlan_Status' AND object_id = OBJECT_ID(N'dbo.TicketPlan'))
    CREATE INDEX IX_TicketPlan_Status ON dbo.TicketPlan (Status, PlanDate DESC);
GO

IF OBJECT_ID(N'dbo.TicketPlanLine', N'U') IS NULL
CREATE TABLE dbo.TicketPlanLine
(
    LineId         BIGINT IDENTITY(1,1) PRIMARY KEY,
    TicketId       BIGINT        NOT NULL,
    Seq            INT           NOT NULL,
    MatchId        BIGINT        NULL,                     -- 结算按 MatchId 找推荐记录取让球命中
    MatchNo        NVARCHAR(20)  NULL,
    HomeTeam       NVARCHAR(100) NULL,
    AwayTeam       NVARCHAR(100) NULL,
    PickOption     NVARCHAR(50)  NULL,                     -- 让胜 / 让平 / 让负
    Odds           DECIMAL(8,3)  NULL,
    LegProbability DECIMAL(6,2)  NULL,                     -- 该腿去水概率（%）
    LineHit        BIT           NULL,
    CONSTRAINT FK_TicketPlanLine_Ticket FOREIGN KEY (TicketId)
        REFERENCES dbo.TicketPlan (TicketId) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TicketPlanLine_Ticket' AND object_id = OBJECT_ID(N'dbo.TicketPlanLine'))
    CREATE INDEX IX_TicketPlanLine_Ticket ON dbo.TicketPlanLine (TicketId, Seq);
GO

/* ------------------------------------------------------------
   2026-11-03 补两列：票腿要能自己结算。

   起因：票腿来自「有让球方向的全部场次」，而 Recommendation 只存进了精选的场次。
   竞彩精选 0 场的那些天，票照样会发，但票腿在 Recommendation 里找不到对应记录，
   HandicapHit 永远是 NULL，票就永远停在 PENDING。所以让票自带让球数和方向，
   直接对着 FbMatch 的完场比分判，不依赖推荐表。
   ------------------------------------------------------------ */

IF COL_LENGTH(N'dbo.TicketPlanLine', N'Handicap') IS NULL
    ALTER TABLE dbo.TicketPlanLine ADD Handicap DECIMAL(4,2) NULL;
GO

IF COL_LENGTH(N'dbo.TicketPlanLine', N'PickSide') IS NULL
    ALTER TABLE dbo.TicketPlanLine ADD PickSide CHAR(1) NULL;
GO
