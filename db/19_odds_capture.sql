/* ============================================================================
   19_odds_capture.sql —— 逐场采集台账 + 走向汇总（2026-11-05）

   需求原话：「把每一场次的数据采集都放在数据里，做记录、并分析走向。」

   之前的状态：FbJcOddsHistory 里有 124 万行明细，每一行都是某场某档某一分钟的价，
   但**没有任何一处记录「这一轮采了几场、采了几个选项、几点采的」**。
   后果是——11-05 上午 11:09 采完，11:15 采完，下一轮 11:37 才来，中间 22 分钟空白。
   在页面上，这和「这 22 分钟没有变化」长得一模一样。分不清漏采和无变化，
   就等于没有采集记录。

   两张表：
     1) FbOddsScanLog  —— 批次台账。一轮一行。回答「采了没、采了几场、几点、多久」。
     2) FbMatchTrend   —— 每场 × 每玩法的走向汇总。回答「这场这个玩法，从开盘到现在怎么走的」。

   Task: 走向标签只在有算得出来的定义时才给（SPF 三值去水概率迁移 ≥2pp），
   其余一律「无明显迁移」。不做主观判断，避免又变成事后讲故事。
   ============================================================================ */

/* ---------------------------------------------------------------------------
   1) 采集批次台账
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.FbOddsScanLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FbOddsScanLog
    (
        ScanId      BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FbOddsScanLog PRIMARY KEY,
        -- jc = 竞彩固定奖金（FbJcOddsHistory）；eu = 欧赔亚盘（FbOddsHistory）
        Source      VARCHAR(16)   NOT NULL,
        -- 本轮采样时刻（按分钟截断，与 FbJcOddsHistory.CapturedAt 对齐）
        CapturedAt  DATETIME2(0)  NOT NULL,
        MatchCount  INT           NOT NULL,
        OptionCount INT           NOT NULL,
        MoveCount   INT           NOT NULL CONSTRAINT DF_FbOddsScanLog_Move DEFAULT (0),
        ElapsedMs   INT           NULL,
        -- ok / empty / error —— 空盘和采失败要分开，否则漏采会被当成「今天没比赛」
        Status      VARCHAR(12)   NOT NULL CONSTRAINT DF_FbOddsScanLog_Status DEFAULT ('ok'),
        Note        NVARCHAR(200) NULL,
        LoggedAt    DATETIME2(0)  NOT NULL CONSTRAINT DF_FbOddsScanLog_Logged DEFAULT (SYSDATETIME())
    );

    -- 同一来源同一分钟只留一条：重跑采样是幂等的，台账也必须幂等
    CREATE UNIQUE INDEX UX_FbOddsScanLog_Key ON dbo.FbOddsScanLog (Source, CapturedAt);
    CREATE INDEX IX_FbOddsScanLog_At ON dbo.FbOddsScanLog (CapturedAt DESC) INCLUDE (Source, MatchCount, OptionCount, MoveCount);
END
GO

/* ---------------------------------------------------------------------------
   2) 每场 × 每玩法走向汇总

   为什么物化而不是视图：一屏 40 场 × 5 玩法 = 200 组，每组要从 100 多万行里
   聚合首末值 + 变盘计数。视图每刷一次页面就算一遍全表，物化只在采样时增量刷本次场次。
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.FbMatchTrend', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FbMatchTrend
    (
        MatchId      BIGINT       NOT NULL,
        PlayType     VARCHAR(8)   NOT NULL,
        MatchNo      NVARCHAR(32) NULL,
        -- 本轮纳入统计的时间区间
        FirstAt      DATETIME2(0) NOT NULL,
        LastAt       DATETIME2(0) NOT NULL,
        Rounds       INT          NOT NULL,  -- 采到过几轮（去重分钟数）
        Options      INT          NOT NULL,  -- 该玩法有几个档
        Moves        INT          NOT NULL,  -- ≥5% 的变盘条数
        BigMoves     INT          NOT NULL,  -- ≥10% 的变盘条数（真动过）
        UpCount      INT          NOT NULL,
        DownCount    INT          NOT NULL,

        -- 首轮 / 当前轮的「最低赔档」= 市场最看好那一档
        FirstTopCode NVARCHAR(16) NULL,
        FirstTopName NVARCHAR(32) NULL,
        FirstTopOdds DECIMAL(8,3) NULL,
        LastTopCode  NVARCHAR(16) NULL,
        LastTopName  NVARCHAR(32) NULL,
        LastTopOdds  DECIMAL(8,3) NULL,
        -- 同一档的赔率迁移率；首末不是同一档时为 NULL（换档本身就是更强的信号，看 Code 列）
        TopShift     DECIMAL(8,4) NULL,

        -- 三向玩法（SPF / NSPF）的初盘与现盘，其余玩法为 NULL
        OpenWin      DECIMAL(8,3) NULL,
        OpenDraw     DECIMAL(8,3) NULL,
        OpenLoss     DECIMAL(8,3) NULL,
        CurrWin      DECIMAL(8,3) NULL,
        CurrDraw     DECIMAL(8,3) NULL,
        CurrLoss     DECIMAL(8,3) NULL,

        -- 走向标签。只有 SPF 去水概率迁移 ≥2pp 才有名字，否则「无明显迁移」。
        TrendTag     NVARCHAR(24) NULL,
        UpdatedAt    DATETIME2(0) NOT NULL CONSTRAINT DF_FbMatchTrend_Updated DEFAULT (SYSDATETIME()),

        CONSTRAINT PK_FbMatchTrend PRIMARY KEY (MatchId, PlayType)
    );

    CREATE INDEX IX_FbMatchTrend_LastAt ON dbo.FbMatchTrend (LastAt DESC) INCLUDE (TrendTag, Moves);
END
GO

/* ---------------------------------------------------------------------------
   3) 历史回填：只在表为空时跑一次

   与 C# 里每次采样后的增量刷新用同一套口径（见 JcOddsRepository.RefreshTrendAsync）。
   两边口径必须一致，否则回填出来的历史和今天新采的走势对不上，看着像数据坏了。
   --------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM dbo.FbMatchTrend)
BEGIN
    ;WITH s AS (
        SELECT MatchId, PlayType, OptionCode, OptionName, Odds, CapturedAt,
               ROW_NUMBER() OVER (PARTITION BY MatchId, PlayType, OptionCode ORDER BY CapturedAt)      AS rf,
               ROW_NUMBER() OVER (PARTITION BY MatchId, PlayType, OptionCode ORDER BY CapturedAt DESC) AS rl
        FROM dbo.FbJcOddsHistory
        WHERE Odds > 1
    ), f AS (SELECT * FROM s WHERE rf = 1),
       l AS (SELECT * FROM s WHERE rl = 1),
       agg AS (
        SELECT MatchId, PlayType,
               MIN(CapturedAt) AS FirstAt, MAX(CapturedAt) AS LastAt,
               COUNT(DISTINCT CapturedAt) AS Rounds,
               COUNT(DISTINCT OptionCode) AS Options
        FROM dbo.FbJcOddsHistory
        WHERE Odds > 1
        GROUP BY MatchId, PlayType
       ),
       ft AS (SELECT MatchId, PlayType, OptionCode, OptionName, Odds FROM (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY MatchId, PlayType ORDER BY Odds, OptionCode) rn
                FROM f) z WHERE rn = 1),
       lt AS (SELECT MatchId, PlayType, OptionCode, OptionName, Odds FROM (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY MatchId, PlayType ORDER BY Odds, OptionCode) rn
                FROM l) z WHERE rn = 1),
       pf AS (SELECT MatchId, PlayType,
                     MAX(CASE WHEN OptionCode = '3' THEN Odds END) AS W,
                     MAX(CASE WHEN OptionCode = '1' THEN Odds END) AS D,
                     MAX(CASE WHEN OptionCode = '0' THEN Odds END) AS L
              FROM f WHERE PlayType IN ('SPF', 'NSPF') GROUP BY MatchId, PlayType),
       pl AS (SELECT MatchId, PlayType,
                     MAX(CASE WHEN OptionCode = '3' THEN Odds END) AS W,
                     MAX(CASE WHEN OptionCode = '1' THEN Odds END) AS D,
                     MAX(CASE WHEN OptionCode = '0' THEN Odds END) AS L
              FROM l WHERE PlayType IN ('SPF', 'NSPF') GROUP BY MatchId, PlayType),
       mv AS (SELECT MatchId, PlayType, COUNT(*) AS Moves,
                     SUM(CASE WHEN ABS(ChangePct) >= 0.10 THEN 1 ELSE 0 END) AS BigMoves,
                     SUM(CASE WHEN ChangePct > 0 THEN 1 ELSE 0 END) AS UpCount,
                     SUM(CASE WHEN ChangePct < 0 THEN 1 ELSE 0 END) AS DownCount
              FROM dbo.FbOddsMove GROUP BY MatchId, PlayType),
       -- 去水概率迁移：初盘 vs 现盘，主/平/客各三个百分点差
       p AS (
        SELECT a.MatchId, a.PlayType, a.FirstAt, a.LastAt, a.Rounds, a.Options,
               ft.OptionCode AS FCode, ft.OptionName AS FName, ft.Odds AS FOdds,
               lt.OptionCode AS LCode, lt.OptionName AS LName, lt.Odds AS LOdds,
               pf.W AS OW, pf.D AS OD, pf.L AS OL,
               pl.W AS CW, pl.D AS CD, pl.L AS CL
        FROM agg a
        LEFT JOIN ft ON ft.MatchId = a.MatchId AND ft.PlayType = a.PlayType
        LEFT JOIN lt ON lt.MatchId = a.MatchId AND lt.PlayType = a.PlayType
        LEFT JOIN pf ON pf.MatchId = a.MatchId AND pf.PlayType = a.PlayType
        LEFT JOIN pl ON pl.MatchId = a.MatchId AND pl.PlayType = a.PlayType
       )
    INSERT INTO dbo.FbMatchTrend
        (MatchId, PlayType, MatchNo, FirstAt, LastAt, Rounds, Options, Moves, BigMoves,
         UpCount, DownCount, FirstTopCode, FirstTopName, FirstTopOdds,
         LastTopCode, LastTopName, LastTopOdds, TopShift,
         OpenWin, OpenDraw, OpenLoss, CurrWin, CurrDraw, CurrLoss, TrendTag, UpdatedAt)
    SELECT p.MatchId, p.PlayType,
           (SELECT MAX(n.MatchNo) FROM dbo.FbJcOddsHistory n
            WHERE n.MatchId = p.MatchId AND n.MatchNo IS NOT NULL),
           p.FirstAt, p.LastAt, p.Rounds, p.Options,
           ISNULL(mv.Moves, 0), ISNULL(mv.BigMoves, 0), ISNULL(mv.UpCount, 0), ISNULL(mv.DownCount, 0),
           p.FCode, p.FName, p.FOdds, p.LCode, p.LName, p.LOdds,
           CASE WHEN p.FCode = p.LCode AND p.FOdds > 1
                THEN (p.LOdds - p.FOdds) / p.FOdds END,
           p.OW, p.OD, p.OL, p.CW, p.CD, p.CL,
           CASE
               WHEN p.OW > 1 AND p.OD > 1 AND p.OL > 1 AND p.CW > 1 AND p.CD > 1 AND p.CL > 1 THEN
                   CASE
                       WHEN ABS((1.0/p.CW) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL))
                              - (1.0/p.OW) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))) < 0.02
                        AND ABS((1.0/p.CD) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL))
                              - (1.0/p.OD) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))) < 0.02
                        AND ABS((1.0/p.CL) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL))
                              - (1.0/p.OL) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))) < 0.02
                       THEN N'无明显迁移'
                       WHEN (1.0/p.CW) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL))
                          - (1.0/p.OW) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))
                          >= (1.0/p.CD) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL)) - (1.0/p.OD) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))
                        AND (1.0/p.CW) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL))
                          - (1.0/p.OW) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))
                          >= (1.0/p.CL) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL)) - (1.0/p.OL) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))
                       THEN N'主胜走强'
                       WHEN (1.0/p.CL) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL))
                          - (1.0/p.OL) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))
                          >= (1.0/p.CD) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL)) - (1.0/p.OD) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))
                       THEN N'客胜走强'
                       ELSE N'平局走强'
                   END
               WHEN p.FCode <> p.LCode THEN CONCAT(N'热门档切换 ', p.FName, N'→', p.LName)
           END,
           SYSDATETIME()
    FROM p
    LEFT JOIN mv ON mv.MatchId = p.MatchId AND mv.PlayType = p.PlayType;

    PRINT CONCAT(N'走向回填完成，共 ', @@ROWCOUNT, N' 组（场×玩法）');
END
GO

/* ---------------------------------------------------------------------------
   4) 台账回填：从现有快照反推「过去采过哪几轮」

   这是补历史，不是补采集。能补出来的只有「这一分钟有快照」，采了几场也数得出来；
   但「这一轮是不是漏了」补不出来——那正是这张表以后要解决的问题。
   --------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM dbo.FbOddsScanLog WHERE Source = 'jc')
BEGIN
    INSERT INTO dbo.FbOddsScanLog (Source, CapturedAt, MatchCount, OptionCount, MoveCount, Status, Note)
    SELECT 'jc', h.CapturedAt,
           COUNT(DISTINCT h.MatchId), COUNT(*),
           (SELECT COUNT(*) FROM dbo.FbOddsMove d
            WHERE d.MoveAt = h.CapturedAt AND d.CapturedAt = h.CapturedAt),
           'ok', N'台账回填（由快照反推）'
    FROM dbo.FbJcOddsHistory h
    GROUP BY h.CapturedAt;

    PRINT CONCAT(N'采集台账回填完成，共 ', @@ROWCOUNT, N' 轮');
END
GO
