/* ------------------------------------------------------------
   否决台账：记录「被规则挡掉的场次」，而不是只记录发出去的场次。

   起因（2026-11-03）：一天 8 场竞彩全部被「必发极端大热 ≥75% 不追」挡掉，
   而否决的场次根本不写库（Recommendation 只存精选），于是：
     - 近 30 天 Recommendation 里「极端大热」出现 0 次
     - 「这条阈值挡掉了什么、挡得对不对」无从回溯
     - 要验证就得手工跑 SQL，还得自己防「赛后必发」污染

   为什么不复用 Recommendation：那张表被校准（/api/calibration）、
   命中率复盘、让球结算共用，塞进未发布的场次会直接稀释这些结论。
   台账独立一张表，只做旁路观测，不参与任何发布决策。
   ------------------------------------------------------------ */

IF OBJECT_ID(N'dbo.RecommendationVeto', N'U') IS NULL
CREATE TABLE dbo.RecommendationVeto
(
    VetoId         BIGINT IDENTITY(1,1) PRIMARY KEY,
    Board          VARCHAR(10)  NOT NULL,              -- jc / bd
    RecDate        DATE         NOT NULL,
    MatchId        BIGINT       NULL,
    MatchNo        NVARCHAR(20) NOT NULL,
    HomeTeam       NVARCHAR(100) NULL,
    AwayTeam       NVARCHAR(100) NULL,
    MatchTime      DATETIME2(0) NULL,

    -- 被挡掉的方向。注意这里存的是「规则生效前本来会打的那一边」，
    -- 不是模型最终输出（否决后 RecResult 会被清成 NULL，方向就丢了）。
    Pick           CHAR(1)      NULL,                  -- 3 主胜 / 1 平 / 0 客胜
    HcpPick        CHAR(1)      NULL,                  -- 让球方向，北单用
    Handicap       DECIMAL(4,2) NULL,

    ReasonCode     VARCHAR(30)  NOT NULL,              -- 机器可读的否决原因，见 docs/否决台账.md
    Reason         NVARCHAR(500) NULL,                 -- 当时的 SignalDesc 原文

    SignalLevel    INT          NULL,
    HitProbability DECIMAL(6,2) NULL,
    HcpProbability DECIMAL(6,2) NULL,
    PickOdds       DECIMAL(8,2) NULL,                  -- 该方向当时的水位，算回报用
    MarketProb     DECIMAL(6,2) NULL,
    RatioHome      DECIMAL(6,2) NULL,
    RatioAway      DECIMAL(6,2) NULL,
    OddsDelta      DECIMAL(8,2) NULL,

    ActualResult   CHAR(1)      NULL,                  -- 赛后回填
    ActualScore    NVARCHAR(10) NULL,
    Hit            BIT          NULL,                  -- 若当时真按这个方向买，中没中
    HcpHit         BIT          NULL,

    CreatedAt      DATETIME2(0) NOT NULL CONSTRAINT DF_RecommendationVeto_CreatedAt DEFAULT SYSDATETIME()
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RecommendationVeto_Board_Date_No'
               AND object_id = OBJECT_ID(N'dbo.RecommendationVeto'))
    CREATE UNIQUE INDEX UX_RecommendationVeto_Board_Date_No
        ON dbo.RecommendationVeto (Board, RecDate, MatchNo);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RecommendationVeto_RecDate'
               AND object_id = OBJECT_ID(N'dbo.RecommendationVeto'))
    CREATE INDEX IX_RecommendationVeto_RecDate
        ON dbo.RecommendationVeto (RecDate) INCLUDE (ReasonCode, Hit, PickOdds);
GO
