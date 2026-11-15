/* ============================================================
   AiFootBall 数据分析系统 - 数据库结构
   目标库：AiFootBall (SQL Server 2019+)
   说明：脚本幂等，可重复执行
   ============================================================ */

IF DB_ID(N'AiFootBall') IS NULL
BEGIN
    CREATE DATABASE AiFootBall;
END
GO

USE AiFootBall;
GO

/* ---------- 0. 通用：同步日志 ---------- */
IF OBJECT_ID(N'dbo.SyncLog', N'U') IS NULL
CREATE TABLE dbo.SyncLog
(
    LogId        BIGINT IDENTITY(1,1) PRIMARY KEY,
    TaskName     NVARCHAR(100)  NOT NULL,
    Source       NVARCHAR(100)  NULL,
    StartedAt    DATETIME2(0)   NOT NULL CONSTRAINT DF_SyncLog_StartedAt DEFAULT SYSDATETIME(),
    FinishedAt   DATETIME2(0)   NULL,
    Status       VARCHAR(10)    NOT NULL CONSTRAINT DF_SyncLog_Status DEFAULT 'RUNNING', -- RUNNING/SUCCESS/FAILED
    RowsAffected INT            NULL,
    Message      NVARCHAR(1000) NULL
);
GO

/* ---------- 1. 彩票域 ---------- */

/* 1.1 彩种字典 */
IF OBJECT_ID(N'dbo.LotteryType', N'U') IS NULL
CREATE TABLE dbo.LotteryType
(
    LotteryId    INT IDENTITY(1,1) PRIMARY KEY,
    Code         VARCHAR(20)   NOT NULL,           -- dlt/ssq/fc3d/qlc/kl8/pl3/pl5/qxc
    Name         NVARCHAR(50)  NOT NULL,
    Category     NVARCHAR(10)  NOT NULL,           -- 福彩 / 体彩
    RedCount     TINYINT       NOT NULL,           -- 前区/红球个数
    RedMax       TINYINT       NOT NULL,
    BlueCount    TINYINT       NOT NULL,           -- 后区/蓝球个数（0 表示无）
    BlueMax      TINYINT       NOT NULL,
    DrawWeekdays VARCHAR(20)   NULL,               -- 开奖日 1=周一 ... 7=周日
    SourceUrl    NVARCHAR(400) NULL,
    IsActive     BIT           NOT NULL CONSTRAINT DF_LotteryType_IsActive DEFAULT 1,
    CONSTRAINT UQ_LotteryType_Code UNIQUE (Code)
);
GO

/* 1.2 开奖主表（一期一行） */
IF OBJECT_ID(N'dbo.LotteryDraw', N'U') IS NULL
CREATE TABLE dbo.LotteryDraw
(
    DrawId       BIGINT IDENTITY(1,1) PRIMARY KEY,
    LotteryId    INT            NOT NULL,
    Issue        VARCHAR(20)    NOT NULL,          -- 期号，如 2026122
    DrawDate     DATE           NOT NULL,
    RedNumbers   VARCHAR(80)    NOT NULL,          -- '02,14,21,22,24'（快乐8 为 20 个号码）
    BlueNumbers  VARCHAR(40)    NULL,              -- '02,12'
    RedSum       SMALLINT       NULL,              -- 前区和值
    RedSpan      TINYINT        NULL,              -- 前区跨度
    OddCount     TINYINT        NULL,              -- 前区奇数个数
    BigCount     TINYINT        NULL,              -- 前区大号个数
    SalesAmount  BIGINT         NULL,              -- 销售额（元）
    PoolAmount   BIGINT         NULL,              -- 奖池（元）
    Source       VARCHAR(20)    NOT NULL,          -- pinble / 500
    CreatedAt    DATETIME2(0)   NOT NULL CONSTRAINT DF_LotteryDraw_CreatedAt DEFAULT SYSDATETIME(),
    UpdatedAt    DATETIME2(0)   NULL,
    CONSTRAINT UQ_LotteryDraw_Issue UNIQUE (LotteryId, Issue),
    CONSTRAINT FK_LotteryDraw_Type FOREIGN KEY (LotteryId) REFERENCES dbo.LotteryType(LotteryId)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LotteryDraw_Date' AND object_id = OBJECT_ID(N'dbo.LotteryDraw'))
    CREATE INDEX IX_LotteryDraw_Date ON dbo.LotteryDraw (LotteryId, DrawDate DESC);
GO

/* 兼容旧版本：扩容号码列（快乐8 一期 20 个号码） */
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.LotteryDraw')
           AND name = N'RedNumbers' AND max_length < 80)
    ALTER TABLE dbo.LotteryDraw ALTER COLUMN RedNumbers VARCHAR(80) NOT NULL;
GO
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.LotteryDraw')
           AND name = N'BlueNumbers' AND max_length < 40)
    ALTER TABLE dbo.LotteryDraw ALTER COLUMN BlueNumbers VARCHAR(40) NULL;
GO

/* 1.3 开奖号码明细（一球一行，便于频率/遗漏统计） */
IF OBJECT_ID(N'dbo.LotteryDrawNumber', N'U') IS NULL
CREATE TABLE dbo.LotteryDrawNumber
(
    LotteryId INT         NOT NULL,
    Issue     VARCHAR(20) NOT NULL,
    BallType  CHAR(1)     NOT NULL,                -- R=前区/红球  B=后区/蓝球
    Seq       TINYINT     NOT NULL,                -- 第几个球
    Number    TINYINT     NOT NULL,
    CONSTRAINT PK_LotteryDrawNumber PRIMARY KEY (LotteryId, Issue, BallType, Seq),
    CONSTRAINT FK_LotteryDrawNumber_Draw FOREIGN KEY (LotteryId, Issue)
        REFERENCES dbo.LotteryDraw (LotteryId, Issue) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DrawNumber_Value' AND object_id = OBJECT_ID(N'dbo.LotteryDrawNumber'))
    CREATE INDEX IX_DrawNumber_Value ON dbo.LotteryDrawNumber (LotteryId, BallType, Number) INCLUDE (Issue);
GO

/* 1.4 奖级明细（500 源提供部分奖级） */
IF OBJECT_ID(N'dbo.LotteryPrize', N'U') IS NULL
CREATE TABLE dbo.LotteryPrize
(
    LotteryId   INT         NOT NULL,
    Issue       VARCHAR(20) NOT NULL,
    PrizeLevel  TINYINT     NOT NULL,              -- 1=一等奖 ...
    BetCount    INT         NULL,                  -- 中奖注数
    PrizeAmount BIGINT      NULL,                  -- 单注奖金（元）
    CONSTRAINT PK_LotteryPrize PRIMARY KEY (LotteryId, Issue, PrizeLevel),
    CONSTRAINT FK_LotteryPrize_Draw FOREIGN KEY (LotteryId, Issue)
        REFERENCES dbo.LotteryDraw (LotteryId, Issue) ON DELETE CASCADE
);
GO

/* ---------- 2. 足球域 ---------- */

/* 2.1 联赛 */
IF OBJECT_ID(N'dbo.FbLeague', N'U') IS NULL
CREATE TABLE dbo.FbLeague
(
    LeagueId INT           NOT NULL PRIMARY KEY,   -- 500 的 sid
    Name     NVARCHAR(60)  NOT NULL,
    Country  NVARCHAR(30)  NULL,
    Season   NVARCHAR(20)  NULL,
    UpdatedAt DATETIME2(0) NULL
);
GO

/* 2.2 球队 */
IF OBJECT_ID(N'dbo.FbTeam', N'U') IS NULL
CREATE TABLE dbo.FbTeam
(
    TeamId   INT           NOT NULL PRIMARY KEY,   -- 500 的 team id
    Name     NVARCHAR(60)  NOT NULL,
    LeagueId INT           NULL,
    UpdatedAt DATETIME2(0) NULL
);
GO

/* 2.3 赛事（赛程 + 赛果） */
IF OBJECT_ID(N'dbo.FbMatch', N'U') IS NULL
CREATE TABLE dbo.FbMatch
(
    MatchId       BIGINT        NOT NULL PRIMARY KEY,   -- 500 的 fid
    LeagueId      INT           NULL,
    LeagueName    NVARCHAR(60)  NULL,                   -- 冗余联赛名（部分赛事无联赛链接）
    Season        NVARCHAR(20)  NULL,
    MatchTime     DATETIME2(0)  NOT NULL,
    HomeTeamId    INT           NULL,
    AwayTeamId    INT           NULL,
    HomeTeamName  NVARCHAR(60)  NULL,
    AwayTeamName  NVARCHAR(60)  NULL,
    HomeScore     TINYINT       NULL,
    AwayScore     TINYINT       NULL,
    HalfHomeScore TINYINT       NULL,
    HalfAwayScore TINYINT       NULL,
    Status        TINYINT       NOT NULL CONSTRAINT DF_FbMatch_Status DEFAULT 0, -- 0未开赛 1进行中 -1完场 2取消
    Handicap      DECIMAL(5,2)  NULL,                   -- 让球数
    ResultSpf     CHAR(1)       NULL,                   -- 3/1/0 胜平负
    ResultHandicap CHAR(1)      NULL,                   -- 让球胜平负
    TotalGoals    TINYINT       NULL,
    ScoreText     NVARCHAR(10)  NULL,
    Source        VARCHAR(20)   NOT NULL CONSTRAINT DF_FbMatch_Source DEFAULT '500',
    CreatedAt     DATETIME2(0)  NOT NULL CONSTRAINT DF_FbMatch_CreatedAt DEFAULT SYSDATETIME(),
    UpdatedAt     DATETIME2(0)  NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FbMatch_Time' AND object_id = OBJECT_ID(N'dbo.FbMatch'))
    CREATE INDEX IX_FbMatch_Time ON dbo.FbMatch (MatchTime DESC, LeagueId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.FbMatch') AND name = N'LeagueName')
    ALTER TABLE dbo.FbMatch ADD LeagueName NVARCHAR(60) NULL;
GO

/* 2.4 赔率（欧赔 / 亚盘 / 大小球，初盘 + 终盘，多公司）
   说明：旧版 FbOdds 只有欧赔三列，检测到旧结构且表为空时直接重建 */
IF OBJECT_ID(N'dbo.FbOdds', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.FbOdds') AND name = N'OddsType')
   AND NOT EXISTS (SELECT 1 FROM dbo.FbOdds)
    DROP TABLE dbo.FbOdds;
GO

IF OBJECT_ID(N'dbo.FbOdds', N'U') IS NULL
CREATE TABLE dbo.FbOdds
(
    OddsId      BIGINT IDENTITY(1,1) PRIMARY KEY,
    MatchId     BIGINT        NOT NULL,
    CompanyId   INT           NOT NULL CONSTRAINT DF_FbOdds_CompanyId DEFAULT 0,  -- 500 公司 cid，0=平均值
    Company     NVARCHAR(40)  NOT NULL,
    OddsType    CHAR(2)       NOT NULL,            -- EU=欧赔 AH=亚盘 OU=大小球
    IsOpening   BIT           NOT NULL,            -- 1=初盘 0=终盘（未开赛时为即时）

    /* 欧赔 */
    HomeWin     DECIMAL(8,3)  NULL,
    Draw        DECIMAL(8,3)  NULL,
    AwayWin     DECIMAL(8,3)  NULL,
    ReturnRate  DECIMAL(6,2)  NULL,                -- 返还率 %
    KellyWin    DECIMAL(8,3)  NULL,                -- 凯利指数
    KellyDraw   DECIMAL(8,3)  NULL,
    KellyLoss   DECIMAL(8,3)  NULL,

    /* 亚盘：负=主队让球 */
    Handicap    DECIMAL(6,3)  NULL,
    HomeWater   DECIMAL(6,3)  NULL,
    AwayWater   DECIMAL(6,3)  NULL,

    /* 大小球 */
    TotalLine   DECIMAL(6,3)  NULL,
    OverWater   DECIMAL(6,3)  NULL,
    UnderWater  DECIMAL(6,3)  NULL,

    CapturedAt  DATETIME2(0)  NOT NULL CONSTRAINT DF_FbOdds_CapturedAt DEFAULT SYSDATETIME(),
    CONSTRAINT FK_FbOdds_Match FOREIGN KEY (MatchId) REFERENCES dbo.FbMatch (MatchId) ON DELETE CASCADE,
    CONSTRAINT UQ_FbOdds UNIQUE (MatchId, CompanyId, OddsType, IsOpening)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FbOdds_Match' AND object_id = OBJECT_ID(N'dbo.FbOdds'))
    CREATE INDEX IX_FbOdds_Match ON dbo.FbOdds (MatchId, OddsType, IsOpening);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FbOdds_Company' AND object_id = OBJECT_ID(N'dbo.FbOdds'))
    CREATE INDEX IX_FbOdds_Company ON dbo.FbOdds (CompanyId, OddsType) INCLUDE (MatchId, IsOpening);
GO

/* 2.5 赛事赔率采集标记（避免重复抓取无赔率的历史赛事） */
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.FbMatch') AND name = N'OddsCheckedAt')
    ALTER TABLE dbo.FbMatch ADD OddsCheckedAt DATETIME2(0) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.FbMatch') AND name = N'OddsStatus')
    ALTER TABLE dbo.FbMatch ADD OddsStatus TINYINT NULL;   -- 0=无赔率 1=已采集 2=抓取失败
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.FbMatch') AND name = N'OddsCompanyCount')
    ALTER TABLE dbo.FbMatch ADD OddsCompanyCount SMALLINT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FbMatch_OddsPending' AND object_id = OBJECT_ID(N'dbo.FbMatch'))
    CREATE INDEX IX_FbMatch_OddsPending ON dbo.FbMatch (Status, OddsCheckedAt) INCLUDE (MatchTime);
GO

/* ============================================================
   2.6 赔率变化时间序列（初盘 → 赛前 → 赛果的完整轨迹）
   由滚动采样写入。只保留「平均」与几家主流公司，控制数据量。
   ============================================================ */
IF OBJECT_ID(N'dbo.FbOddsHistory', N'U') IS NULL
CREATE TABLE dbo.FbOddsHistory
(
    Id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    MatchId     BIGINT        NOT NULL,
    CapturedAt  DATETIME2(0)  NOT NULL,
    MinutesToKick INT         NULL,               -- 距开赛分钟；负数=已开赛
    Phase       TINYINT       NOT NULL CONSTRAINT DF_FbOddsHistory_Phase DEFAULT 0,
                                                  -- 0=赛前 1=临场(≤60min) 2=滚球/赛后
    OddsType    CHAR(2)       NOT NULL,           -- EU/AH/OU
    CompanyId   INT           NOT NULL,
    Company     NVARCHAR(40)  NOT NULL,

    HomeWin     DECIMAL(8,3)  NULL,
    Draw        DECIMAL(8,3)  NULL,
    AwayWin     DECIMAL(8,3)  NULL,
    ReturnRate  DECIMAL(6,2)  NULL,
    KellyWin    DECIMAL(8,3)  NULL,
    KellyDraw   DECIMAL(8,3)  NULL,
    KellyLoss   DECIMAL(8,3)  NULL,

    Handicap    DECIMAL(6,3)  NULL,
    HomeWater   DECIMAL(6,3)  NULL,
    AwayWater   DECIMAL(6,3)  NULL,

    TotalLine   DECIMAL(6,3)  NULL,
    OverWater   DECIMAL(6,3)  NULL,
    UnderWater  DECIMAL(6,3)  NULL,

    CONSTRAINT FK_FbOddsHistory_Match FOREIGN KEY (MatchId) REFERENCES dbo.FbMatch (MatchId) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_FbOddsHistory' AND object_id = OBJECT_ID(N'dbo.FbOddsHistory'))
    CREATE UNIQUE INDEX UQ_FbOddsHistory ON dbo.FbOddsHistory (MatchId, CompanyId, OddsType, CapturedAt);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FbOddsHistory_Match' AND object_id = OBJECT_ID(N'dbo.FbOddsHistory'))
    CREATE INDEX IX_FbOddsHistory_Match ON dbo.FbOddsHistory (MatchId, OddsType, CapturedAt) INCLUDE (CompanyId, HomeWin, Draw, AwayWin, Handicap);
GO

/* ============================================================
   2.7 必发（Betfair）成交数据快照 + 大额交易明细
   ============================================================ */
IF OBJECT_ID(N'dbo.FbExchange', N'U') IS NULL
CREATE TABLE dbo.FbExchange
(
    ExchangeId    BIGINT IDENTITY(1,1) PRIMARY KEY,
    MatchId       BIGINT        NOT NULL,
    CapturedAt    DATETIME2(0)  NOT NULL,
    MinutesToKick INT           NULL,

    /* 百家欧指：指数 / 概率 */
    EuroHome      DECIMAL(8,3)  NULL,
    EuroDraw      DECIMAL(8,3)  NULL,
    EuroAway      DECIMAL(8,3)  NULL,
    EuroProbHome  DECIMAL(6,2)  NULL,
    EuroProbDraw  DECIMAL(6,2)  NULL,
    EuroProbAway  DECIMAL(6,2)  NULL,

    /* 必发成交 */
    PriceHome     DECIMAL(8,3)  NULL,
    PriceDraw     DECIMAL(8,3)  NULL,
    PriceAway     DECIMAL(8,3)  NULL,
    VolumeHome    DECIMAL(18,2) NULL,
    VolumeDraw    DECIMAL(18,2) NULL,
    VolumeAway    DECIMAL(18,2) NULL,
    PnlHome       DECIMAL(18,2) NULL,   -- 庄家盈亏（正=庄赢）
    PnlDraw       DECIMAL(18,2) NULL,
    PnlAway       DECIMAL(18,2) NULL,

    /* 交易比例（必发） */
    RatioHome     DECIMAL(6,2)  NULL,
    RatioDraw     DECIMAL(6,2)  NULL,
    RatioAway     DECIMAL(6,2)  NULL,
    /* 北单交易比例 */
    BdRatioHome   DECIMAL(6,2)  NULL,
    BdRatioDraw   DECIMAL(6,2)  NULL,
    BdRatioAway   DECIMAL(6,2)  NULL,

    /* 指数分析 */
    BfIdxHome     DECIMAL(6,2)  NULL,
    BfIdxDraw     DECIMAL(6,2)  NULL,
    BfIdxAway     DECIMAL(6,2)  NULL,
    HotIdxHome    DECIMAL(6,2)  NULL,   -- 冷热指数
    HotIdxDraw    DECIMAL(6,2)  NULL,
    HotIdxAway    DECIMAL(6,2)  NULL,
    PnlIdxHome    DECIMAL(6,2)  NULL,   -- 盈亏指数
    PnlIdxDraw    DECIMAL(6,2)  NULL,
    PnlIdxAway    DECIMAL(6,2)  NULL,

    /* 规模 */
    TotalVolume   DECIMAL(18,2) NULL,   -- 总交易[港币]
    BigTotal      DECIMAL(18,2) NULL,   -- 大额总交易
    BigHome       DECIMAL(18,2) NULL,
    BigDraw       DECIMAL(18,2) NULL,
    BigAway       DECIMAL(18,2) NULL,

    TipScale      NVARCHAR(200) NULL,   -- 数据提点：交易规模
    TipLean       NVARCHAR(200) NULL,   -- 数据提点：成交量倾向

    CONSTRAINT FK_FbExchange_Match FOREIGN KEY (MatchId) REFERENCES dbo.FbMatch (MatchId) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_FbExchange' AND object_id = OBJECT_ID(N'dbo.FbExchange'))
    CREATE UNIQUE INDEX UQ_FbExchange ON dbo.FbExchange (MatchId, CapturedAt);
GO

IF OBJECT_ID(N'dbo.FbExchangeTrade', N'U') IS NULL
CREATE TABLE dbo.FbExchangeTrade
(
    TradeId     BIGINT IDENTITY(1,1) PRIMARY KEY,
    MatchId     BIGINT        NOT NULL,
    TradeTime   DATETIME2(0)  NOT NULL,        -- 源站给出的成交时间
    Side        CHAR(1)       NOT NULL,        -- H/D/A
    Direction   NVARCHAR(4)   NOT NULL,        -- 买/卖
    Volume      DECIMAL(18,4) NOT NULL,
    Ratio       DECIMAL(6,2)  NULL,            -- 该笔占大额交易比例
    CapturedAt  DATETIME2(0)  NOT NULL,
    CONSTRAINT FK_FbExchangeTrade_Match FOREIGN KEY (MatchId) REFERENCES dbo.FbMatch (MatchId) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_FbExchangeTrade' AND object_id = OBJECT_ID(N'dbo.FbExchangeTrade'))
    CREATE UNIQUE INDEX UQ_FbExchangeTrade ON dbo.FbExchangeTrade (MatchId, TradeTime, Side, Direction, Volume);
GO

/* 采集标记：必发是否已抓、抓到的时点 */
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.FbMatch') AND name = N'ExchangeCheckedAt')
    ALTER TABLE dbo.FbMatch ADD ExchangeCheckedAt DATETIME2(0) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.FbMatch') AND name = N'ExchangeStatus')
    ALTER TABLE dbo.FbMatch ADD ExchangeStatus TINYINT NULL;   -- 0=无必发数据 1=有数据 2=抓取失败
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.FbMatch') AND name = N'ExchangeVolume')
    ALTER TABLE dbo.FbMatch ADD ExchangeVolume DECIMAL(18,2) NULL;   -- 最近一次总成交额
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FbMatch_ExchangePending' AND object_id = OBJECT_ID(N'dbo.FbMatch'))
    CREATE INDEX IX_FbMatch_ExchangePending ON dbo.FbMatch (MatchTime, ExchangeCheckedAt);
GO

/* 采样标记：赔率时间序列最后一次采样时点 */
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.FbMatch') AND name = N'OddsSampledAt')
    ALTER TABLE dbo.FbMatch ADD OddsSampledAt DATETIME2(0) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.FbMatch') AND name = N'OddsSampleCount')
    ALTER TABLE dbo.FbMatch ADD OddsSampleCount SMALLINT NULL;
GO

/* ============================================================
   用户 / 受注场次 / 推荐（第四轮：登录 + 今日推荐）
   ============================================================ */

/* 登录用户 */
IF OBJECT_ID(N'dbo.UserAccount', N'U') IS NULL
CREATE TABLE dbo.UserAccount
(
    UserId      INT IDENTITY(1,1) PRIMARY KEY,
    UserName    NVARCHAR(50)  NOT NULL,
    PasswordHash NVARCHAR(128) NOT NULL,
    Salt        NVARCHAR(64)  NOT NULL,
    NickName    NVARCHAR(50)  NULL,
    CreatedAt   DATETIME2(0)  NOT NULL CONSTRAINT DF_UserAccount_CreatedAt DEFAULT SYSDATETIME(),
    LastLoginAt DATETIME2(0)  NULL,
    CONSTRAINT UQ_UserAccount_UserName UNIQUE (UserName)
);
GO

/* 官方受注场次（竞彩/北单/胜负彩，每期每场一条） */
IF OBJECT_ID(N'dbo.BettingCard', N'U') IS NULL
CREATE TABLE dbo.BettingCard
(
    CardId       BIGINT IDENTITY(1,1) PRIMARY KEY,
    Board        VARCHAR(10)  NOT NULL,            -- jc=竞彩 / bd=北单 / sfc=胜负彩
    Expect       NVARCHAR(20) NULL,                -- 期号（胜负彩用，如 26152）
    MatchNo      NVARCHAR(20) NOT NULL,            -- 场次号（如 周三001 / 北70 / 场次1）
    MatchId      BIGINT       NULL,                -- 关联 500.com fid（可空）
    LeagueName   NVARCHAR(100) NULL,
    HomeTeam     NVARCHAR(100) NULL,
    AwayTeam     NVARCHAR(100) NULL,
    MatchTime    DATETIME2(0)  NULL,
    Handicap     DECIMAL(6,2)  NULL,               -- 让球（主让为负）
    SpfHome      DECIMAL(8,3)  NULL,               -- 胜平负赔率/SP值
    SpfDraw      DECIMAL(8,3)  NULL,
    SpfAway      DECIMAL(8,3)  NULL,
    IsSingle     BIT          NOT NULL CONSTRAINT DF_BettingCard_IsSingle DEFAULT 0, -- 单关
    CapturedAt   DATETIME2(0)  NOT NULL CONSTRAINT DF_BettingCard_CapturedAt DEFAULT SYSDATETIME(),
    CONSTRAINT UQ_BettingCard UNIQUE (Board, Expect, MatchNo)
);
GO

/* 推荐记录（快照，便于复盘命中率） */
IF OBJECT_ID(N'dbo.Recommendation', N'U') IS NULL
CREATE TABLE dbo.Recommendation
(
    RecId        BIGINT IDENTITY(1,1) PRIMARY KEY,
    Board        VARCHAR(10)  NOT NULL,
    Expect       NVARCHAR(20) NULL,
    MatchId      BIGINT       NULL,
    MatchNo      NVARCHAR(20) NULL,
    HomeTeam     NVARCHAR(100) NULL,
    AwayTeam     NVARCHAR(100) NULL,
    MatchTime    DATETIME2(0)  NULL,
    RecResult    CHAR(1)      NULL,                -- 3=主胜 1=平 0=客胜；NULL=全包，不计入命中
    Confidence   DECIMAL(6,2) NULL,                -- 置信度（命中率估计）
    SignalLevel  INT          NULL,                -- 3=双信号 2=单信号 1=弱信号 0=无信号
    IsCoverAll   BIT          NOT NULL CONSTRAINT DF_Recommendation_IsCoverAll DEFAULT 0,
    SignalDesc   NVARCHAR(500) NULL,               -- 信号描述
    Score1       NVARCHAR(10) NULL,                -- 最可能比分 1（如 3:2）
    Score2       NVARCHAR(10) NULL,                -- 最可能比分 2
    Goals1       INT          NULL,                -- 最可能进球数 1
    Goals2       INT          NULL,                -- 最可能进球数 2
    RecHandicap  CHAR(1)      NULL,                -- 让球胜平负推荐
    RecHalfFull  NVARCHAR(20) NULL,                -- 半全场推荐
    ActualScore  NVARCHAR(10) NULL,                -- 实际比分（赛后可回填）
    ActualGoals  INT          NULL,                -- 实际总进球
    ActualResult CHAR(1)      NULL,                -- 实际赛果（赛后可回填）
    Hit          BIT          NULL,                -- 是否命中
    RecDate      DATE         NOT NULL CONSTRAINT DF_Recommendation_RecDate DEFAULT CAST(SYSDATETIME() AS DATE),
    CreatedAt    DATETIME2(0)  NOT NULL CONSTRAINT DF_Recommendation_CreatedAt DEFAULT SYSDATETIME()
);
GO

/* ============================================================
   购买记录（自购方案）主从表 —— 第六轮：我的投注
   ============================================================ */

/* 主表：购买方案（一次投注一个方案/串关） */
IF OBJECT_ID(N'dbo.Purchase', N'U') IS NULL
CREATE TABLE dbo.Purchase
(
    PurchaseId      BIGINT IDENTITY(1,1) PRIMARY KEY,
    Board           VARCHAR(10)   NOT NULL,            -- jc=竞彩 / bd=北单 / sfc=胜负彩
    Expect          NVARCHAR(20)  NULL,                -- 期号/方案号
    PlayType        NVARCHAR(50)  NULL,                -- 玩法：胜平负 / 让球胜平负 / 全场比分 / 总进球
    PassType        NVARCHAR(50)  NULL,                -- 过关方式：单关 / 2串1 / 3串1#4串1
    Multiple        INT           NOT NULL CONSTRAINT DF_Purchase_Multiple DEFAULT 1,  -- 倍数
    BetCount        INT           NULL,                -- 总注数
    BetAmount       DECIMAL(10,2) NOT NULL CONSTRAINT DF_Purchase_BetAmount DEFAULT 0, -- 投注金额(元)
    ExpectedWin     DECIMAL(12,2) NULL,                -- 预计中奖金额(元)
    ActualWin       DECIMAL(12,2) NULL,                -- 实际中奖金额(元)
    Status          VARCHAR(20)   NOT NULL CONSTRAINT DF_Purchase_Status DEFAULT 'PENDING',
    BuyDate         DATE          NOT NULL CONSTRAINT DF_Purchase_BuyDate DEFAULT CAST(SYSDATETIME() AS DATE),
    TicketImage     NVARCHAR(500) NULL,                -- 购买截图路径
    ResultImage     NVARCHAR(500) NULL,                -- 开奖截图路径
    IsHit           BIT           NULL,                -- 是否中奖
    HitLevel        NVARCHAR(50)  NULL,                -- 命中奖级/串关
    Remark          NVARCHAR(500) NULL,                -- 复盘分析
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
    Seq             INT           NOT NULL,
    MatchNo         NVARCHAR(20)  NULL,
    MatchId         BIGINT        NULL,
    LeagueName      NVARCHAR(100) NULL,
    HomeTeam        NVARCHAR(100) NULL,
    AwayTeam        NVARCHAR(100) NULL,
    MatchTime       DATETIME2(0)  NULL,
    Handicap        DECIMAL(6,2)  NULL,
    Option1         NVARCHAR(50)  NULL,
    Option2         NVARCHAR(50)  NULL,
    Odds1           DECIMAL(8,3)  NULL,
    Odds2           DECIMAL(8,3)  NULL,
    ActualScore     NVARCHAR(10)  NULL,
    ActualGoals     INT           NULL,
    ActualResult    CHAR(1)       NULL,
    LineHit         BIT           NULL,
    CONSTRAINT FK_PurchaseLine_Purchase FOREIGN KEY (PurchaseId) REFERENCES dbo.Purchase (PurchaseId) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PurchaseLine_Purchase' AND object_id = OBJECT_ID(N'dbo.PurchaseLine'))
    CREATE INDEX IX_PurchaseLine_Purchase ON dbo.PurchaseLine (PurchaseId, Seq);
GO
