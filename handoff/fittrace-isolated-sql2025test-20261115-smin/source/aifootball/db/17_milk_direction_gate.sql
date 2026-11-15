/* ------------------------------------------------------------
   大奶方向闸台账（2026-11-05）。

   起因：11-04 晚三场全反——朗斯主场被科莫打 1:9、费内巴切反赢利物浦、波尔图没平。
   用库里 1873 场已完赛样本回测三层信号的方向命中率：
       亚盘水位方向 47.7%（≈ 抛硬币）
       欧赔隐含方向 61.0%
       必发大额方向 74.4%
       三层完全一致 84.4%
       三层背离时跟亚盘 26.3%     ← 亚盘在背离时几乎是反向指标
   结论：方向只认市场（竞彩胜平负去水，缺则比分盘反推），
   模型方向与市场方向背离的场次整场不发。

   这张表回答两件事：
   1. 某场为什么没发（Verdict=diverge）；
   2. 当时模型指哪、市场指哪、亚盘和必发有没有背离（AlertCode）。
   赔率快照会被下一轮覆盖，所以诊断必须与出票同时落库，否则赛后无法复盘。
   ------------------------------------------------------------ */

IF OBJECT_ID(N'dbo.FbMilkGate', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FbMilkGate
    (
        GateId                BIGINT IDENTITY (1, 1) PRIMARY KEY,
        GateDate              DATE            NOT NULL,
        MatchId               BIGINT          NOT NULL,
        MatchNo               NVARCHAR(16)    NOT NULL,
        LeagueName            NVARCHAR(64)    NULL,
        HomeTeam              NVARCHAR(64)    NULL,
        AwayTeam              NVARCHAR(64)    NULL,
        MatchTime             DATETIME2       NULL,

        -- 模型方向（Dixon-Coles 网格）：3 主胜 / 1 平 / 0 客胜
        ModelDirection        CHAR(1)         NULL,
        ModelHomePct          DECIMAL(6, 2)   NOT NULL,
        ModelDrawPct          DECIMAL(6, 2)   NOT NULL,
        ModelAwayPct          DECIMAL(6, 2)   NOT NULL,

        -- 这场候选注实际指向的方向：否决拿它跟市场比，不是拿 ModelDirection 比。
        -- 模型整体偏主胜、但要发「平平」的场次（11-04 波尔图）就是靠这一列才拦得住。
        PickDirection         CHAR(1)         NULL,

        -- 市场方向：spf = 竞彩胜平负去水，score = 比分盘反推，none = 算不出来
        MarketDirection       CHAR(1)         NULL,
        MarketSource          VARCHAR(8)      NOT NULL,
        MarketHomePct         DECIMAL(6, 2)   NULL,
        MarketDrawPct         DECIMAL(6, 2)   NULL,
        MarketAwayPct         DECIMAL(6, 2)   NULL,

        -- 亚盘（盘口移动方向，主让为负）与必发大额方向，只做告警，不参与否决
        AsianDirection        CHAR(1)         NULL,
        AsianOpen             DECIMAL(6, 2)   NULL,
        AsianClose            DECIMAL(6, 2)   NULL,
        ExchangeDirection     CHAR(1)         NULL,
        RatioHome             DECIMAL(6, 2)   NULL,
        RatioAway             DECIMAL(6, 2)   NULL,

        -- pass / diverge / unknown
        Verdict               VARCHAR(16)     NOT NULL,
        AlertCode             VARCHAR(24)     NULL,      -- trap-asian / trap-exchange / trap-both
        AlertNote             NVARCHAR(256)   NULL,

        -- 这道闸一共挡下几注、其中概率最高的是哪一注
        BlockedPicks          INT             NOT NULL CONSTRAINT DF_FbMilkGate_BlockedPicks DEFAULT 0,
        BlockedBest           NVARCHAR(64)    NULL,
        BlockedBestProbability DECIMAL(6, 2)  NULL,

        CreatedAt             DATETIME2       NOT NULL CONSTRAINT DF_FbMilkGate_CreatedAt DEFAULT SYSDATETIME()
    );

    CREATE UNIQUE INDEX UX_FbMilkGate ON dbo.FbMilkGate (GateDate, MatchId);
END
GO

-- 幂等兜底：sqlcmd 按 ANSI 读 UTF-8 脚本时，中文注释行可能把紧邻的下一行吞掉，
-- 导致建表少列（本机第一次执行就漏了 AsianDirection 和 BlockedPicks）。
-- 用 `sqlcmd -f 65001` 可避免；这里再补一遍，防止在别的机器上重演。
IF COL_LENGTH(N'dbo.FbMilkGate', N'AsianDirection') IS NULL
    ALTER TABLE dbo.FbMilkGate ADD AsianDirection CHAR(1) NULL;
GO

IF COL_LENGTH(N'dbo.FbMilkGate', N'PickDirection') IS NULL
    ALTER TABLE dbo.FbMilkGate ADD PickDirection CHAR(1) NULL;
GO

IF COL_LENGTH(N'dbo.FbMilkGate', N'BlockedPicks') IS NULL
    ALTER TABLE dbo.FbMilkGate ADD BlockedPicks INT NOT NULL
        CONSTRAINT DF_FbMilkGate_BlockedPicks DEFAULT 0;
GO

IF COL_LENGTH(N'dbo.FbMilkGate', N'BlockedBest') IS NULL
    ALTER TABLE dbo.FbMilkGate ADD BlockedBest NVARCHAR(64) NULL;
GO

IF COL_LENGTH(N'dbo.FbMilkGate', N'BlockedBestProbability') IS NULL
    ALTER TABLE dbo.FbMilkGate ADD BlockedBestProbability DECIMAL(6, 2) NULL;
GO
