/* ------------------------------------------------------------
   大奶推荐（2026-11-04 改向）。

   用户指示：胜负彩、北单推荐下线（采集保留），每日只推荐竞彩大奶——
   比分（BF）、半全场（BQ，限 胜负/负胜/胜平/负平 且赔率 ≥4.0）、总进球（JQ）。

   两件事：
   1. FbMilkPick：大奶推荐落库。每场每玩法每选项一行，出票时快照赔率与模型概率，
      赛后按比分/半场比分/总进球判中，是「大奶到底赚不赚」唯一的账。
   2. TicketPlanLine 加 PlayType / PickCode：大奶票腿不是让球方向，
      原来的 LineHit 判定（比分+让球数）对它不适用。两列都 NULL 的老腿走老逻辑，
      不回填、不改历史。
   ------------------------------------------------------------ */

IF OBJECT_ID(N'dbo.FbMilkPick', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FbMilkPick
    (
        PickId           BIGINT IDENTITY (1, 1) PRIMARY KEY,
        PickDate         DATE            NOT NULL,
        MatchId          BIGINT          NOT NULL,
        MatchNo          NVARCHAR(16)    NOT NULL,
        LeagueName       NVARCHAR(64)    NULL,
        HomeTeam         NVARCHAR(64)    NULL,
        AwayTeam         NVARCHAR(64)    NULL,
        MatchTime        DATETIME2       NULL,
        PlayType         VARCHAR(8)      NOT NULL,   -- BF / BQ / JQ
        OptionCode       VARCHAR(16)     NOT NULL,   -- 3:1 / 3-0 / 4 / 7
        OptionName       NVARCHAR(32)    NOT NULL,   -- 比分 3:1 / 半全场 胜负 / 总进球 4球
        Odds             DECIMAL(8, 2)   NOT NULL,
        ModelProbability DECIMAL(6, 2)   NULL,       -- 模型概率 0-100
        Ev               DECIMAL(8, 3)   NULL,       -- 概率 × 赔率
        ActualScore      VARCHAR(8)      NULL,
        ActualHalfScore  VARCHAR(8)      NULL,
        ActualGoals      INT             NULL,
        Hit              BIT             NULL,
        SettledAt        DATETIME2       NULL,
        CreatedAt        DATETIME2       NOT NULL    CONSTRAINT DF_FbMilkPick_CreatedAt DEFAULT SYSDATETIME()
    );

    CREATE UNIQUE INDEX UX_FbMilkPick
        ON dbo.FbMilkPick (PickDate, MatchId, PlayType, OptionCode);
END
GO

IF COL_LENGTH(N'dbo.TicketPlanLine', N'PlayType') IS NULL
    ALTER TABLE dbo.TicketPlanLine ADD PlayType VARCHAR(8) NULL;
GO

IF COL_LENGTH(N'dbo.TicketPlanLine', N'PickCode') IS NULL
    ALTER TABLE dbo.TicketPlanLine ADD PickCode VARCHAR(16) NULL;
GO
