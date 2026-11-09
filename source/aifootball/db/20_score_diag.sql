/* ============================================================
   20_score_diag.sql —— 净胜球诊断（每场一行）

   数据来源：FbJcOddsHistory 的 PlayType='BF'（比分盘 31 档）。

   为什么单独一张表，而不是塞进 FbMatchTrend：
     FbMatchTrend 是「场 × 玩法」，净胜球是「场」这一层的量，
     而且它要把 31 个比分档折叠成一条分布，粒度和口径都不一样。

   口径（必须与 JcOddsRepository.ScoreDiagSql 完全一致）：
     1. 只取 Odds > 1 的档；整轮不足 20 档的快照整轮丢弃 ——
        新建场次先挂三五个档，按它归一化会算出 PHome=100%、ExpGd=4.0 的假值。
     2. 每轮按 1/odds 归一化去水（同一 CapturedAt 内求和）
     3. 净胜球 = 主 - 客；「胜其它/负其它/平其它」按 +5/-5/0 计
     4. ExpGd = Σ 净胜球 × 去水概率
        P3Plus / PMinus3 = 净胜 ≥3 / 净负 ≥3 的概率
     5. Open 系列取第一轮，其余取最后一轮
     6. DeepNegDrop / DeepPosDrop = 深档里赔率下降 ≥5% 的档数（按档数，不按概率）
        —— 11-04 朗斯场 13:45 是 0:3/0:4/0:5/1:4/1:5 五档同时降 5~14%，
           概率上只动了 1.19pp，但这是个「整批」行为，档数比百分点更接近事实。
     7. DistText = 最后一轮完整分布，格式 "gd:千分比" 逗号分隔

   诚实提示：这份数据是「描述」不是「预测」。
   441 场回测：MAE 1.33 球，四舍五入全对 24.7%，各档校准误差 <2pp。
   尾部移动在 105 场样本里没有区分度，只作事实记录，不得接进任何出注逻辑。
   ============================================================ */

IF OBJECT_ID('dbo.FbScoreDiag', 'U') IS NOT NULL DROP TABLE dbo.FbScoreDiag;
GO

CREATE TABLE dbo.FbScoreDiag
(
    MatchId       BIGINT        NOT NULL CONSTRAINT PK_FbScoreDiag PRIMARY KEY,
    MatchNo       NVARCHAR(40)  NULL,
    Rounds        INT           NOT NULL CONSTRAINT DF_FbScoreDiag_Rounds DEFAULT (0),
    FirstAt       DATETIME2(0)  NULL,
    LastAt        DATETIME2(0)  NULL,

    ExpGd         DECIMAL(6,3)  NULL,
    ExpGdOpen     DECIMAL(6,3)  NULL,
    ExpGdMove     DECIMAL(6,3)  NULL,
    ExpTotal      DECIMAL(6,3)  NULL,

    PHome         DECIMAL(6,4)  NULL,
    PDraw         DECIMAL(6,4)  NULL,
    PAway         DECIMAL(6,4)  NULL,
    PHomeOpen     DECIMAL(6,4)  NULL,

    P3Plus        DECIMAL(6,4)  NULL,
    PMinus3       DECIMAL(6,4)  NULL,
    P3PlusMove    DECIMAL(6,4)  NULL,
    PMinus3Move   DECIMAL(6,4)  NULL,

    DeepNegDrop   INT           NOT NULL CONSTRAINT DF_FbScoreDiag_DND DEFAULT (0),
    DeepPosDrop   INT           NOT NULL CONSTRAINT DF_FbScoreDiag_DPD DEFAULT (0),

    DistText      NVARCHAR(400) NULL,
    TailTag       NVARCHAR(24)  NULL,

    UpdatedAt     DATETIME2(0)  NOT NULL CONSTRAINT DF_FbScoreDiag_Upd DEFAULT (SYSDATETIME())
);
GO

/* ---------- 全量回填 ---------- */
;WITH base AS (
    SELECT MatchId, MatchNo, CapturedAt, OptionCode, Odds,
           CASE WHEN CHARINDEX(':', OptionCode) > 0
                THEN CAST(LEFT(OptionCode, CHARINDEX(':', OptionCode) - 1) AS int)
                   - CAST(SUBSTRING(OptionCode, CHARINDEX(':', OptionCode) + 1, 3) AS int)
                WHEN UNICODE(LEFT(OptionCode, 1)) = 32988 THEN 5
                WHEN UNICODE(LEFT(OptionCode, 1)) = 36127 THEN -5
                ELSE 0 END AS gd,
           CASE WHEN CHARINDEX(':', OptionCode) > 0
                THEN CAST(LEFT(OptionCode, CHARINDEX(':', OptionCode) - 1) AS int)
                   + CAST(SUBSTRING(OptionCode, CHARINDEX(':', OptionCode) + 1, 3) AS int)
                ELSE 3 END AS tg
    FROM dbo.FbJcOddsHistory
    WHERE PlayType = 'BF' AND Odds > 1 AND MatchId > 0
),
cnt AS (
    SELECT MatchId, CapturedAt, COUNT(*) AS n FROM base GROUP BY MatchId, CapturedAt
),
ok AS (
    SELECT b.* FROM base b JOIN cnt c ON c.MatchId = b.MatchId AND c.CapturedAt = b.CapturedAt
    WHERE c.n >= 20
),
norm AS (
    SELECT MatchId, MatchNo, CapturedAt, OptionCode, Odds, gd, tg,
           1.0 / Odds / SUM(1.0 / Odds) OVER (PARTITION BY MatchId, CapturedAt) AS p
    FROM ok
),
per AS (
    SELECT MatchId, CapturedAt,
           SUM(gd * p)                                AS exp_gd,
           SUM(tg * p)                                AS exp_tg,
           SUM(CASE WHEN gd >  0 THEN p ELSE 0 END)   AS p_home,
           SUM(CASE WHEN gd =  0 THEN p ELSE 0 END)   AS p_draw,
           SUM(CASE WHEN gd >= 3 THEN p ELSE 0 END)   AS p3,
           SUM(CASE WHEN gd <= -3 THEN p ELSE 0 END)  AS pm3
    FROM norm
    GROUP BY MatchId, CapturedAt
),
rk AS (
    SELECT *,
           ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt)      AS rf,
           ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt DESC) AS rl,
           MIN(CapturedAt) OVER (PARTITION BY MatchId) AS fat,
           MAX(CapturedAt) OVER (PARTITION BY MatchId) AS lat,
           COUNT(*)        OVER (PARTITION BY MatchId) AS n
    FROM per
),
fr AS (SELECT * FROM rk WHERE rf = 1),
lr AS (SELECT * FROM rk WHERE rl = 1),
fraw AS (
    SELECT n.MatchId, n.OptionCode, n.Odds, n.gd
    FROM norm n JOIN fr ON fr.MatchId = n.MatchId AND fr.CapturedAt = n.CapturedAt
),
lraw AS (
    SELECT n.MatchId, n.OptionCode, n.Odds, n.gd
    FROM norm n JOIN lr ON lr.MatchId = n.MatchId AND lr.CapturedAt = n.CapturedAt
),
deepdrop AS (
    SELECT l.MatchId,
           SUM(CASE WHEN l.gd <= -3 AND l.Odds <= f.Odds * 0.95 THEN 1 ELSE 0 END) AS neg_drop,
           SUM(CASE WHEN l.gd >=  3 AND l.Odds <= f.Odds * 0.95 THEN 1 ELSE 0 END) AS pos_drop
    FROM lraw l JOIN fraw f ON f.MatchId = l.MatchId AND f.OptionCode = l.OptionCode
    GROUP BY l.MatchId
),
dist AS (
    SELECT n.MatchId, n.gd, SUM(n.p) AS p
    FROM norm n JOIN lr ON lr.MatchId = n.MatchId AND lr.CapturedAt = n.CapturedAt
    GROUP BY n.MatchId, n.gd
),
distj AS (
    SELECT MatchId,
           STRING_AGG(CAST(gd AS varchar(4)) + ':' + CAST(CAST(ROUND(p * 1000, 0) AS int) AS varchar(6)), ',')
               WITHIN GROUP (ORDER BY gd) AS j
    FROM dist GROUP BY MatchId
)
MERGE dbo.FbScoreDiag AS t
USING (
    SELECT l.MatchId,
           (SELECT TOP 1 MatchNo FROM ok b WHERE b.MatchId = l.MatchId AND b.MatchNo IS NOT NULL) AS MatchNo,
           l.n AS Rounds, l.fat AS FirstAt, l.lat AS LastAt,
           l.exp_gd, f.exp_gd AS exp_gd_open, l.exp_gd - f.exp_gd AS exp_gd_move,
           l.exp_tg,
           l.p_home, l.p_draw, 1.0 - l.p_home - l.p_draw AS p_away, f.p_home AS p_home_open,
           l.p3, l.pm3, l.p3 - f.p3 AS p3_move, l.pm3 - f.pm3 AS pm3_move,
           ISNULL(d.neg_drop, 0) AS neg_drop, ISNULL(d.pos_drop, 0) AS pos_drop,
           dj.j AS DistText,
           CASE
               WHEN ISNULL(d.neg_drop,0) >= 3 AND ISNULL(d.pos_drop,0) >= 3 THEN N'两端整批降价'
               WHEN ISNULL(d.neg_drop,0) >= 3 THEN N'客队深档整批降价'
               WHEN ISNULL(d.pos_drop,0) >= 3 THEN N'主队深档整批降价'
               WHEN l.p3 - f.p3 >= 0.01 AND l.pm3 - f.pm3 >= 0.01 THEN N'两端加厚'
               WHEN l.p3 - f.p3 >= 0.01 THEN N'主队深档加价'
               WHEN l.pm3 - f.pm3 >= 0.01 THEN N'客队深档加价'
               WHEN l.p3 - f.p3 <= -0.01 AND l.pm3 - f.pm3 <= -0.01 THEN N'两端削薄'
               WHEN l.p3 - f.p3 <= -0.01 THEN N'主队深档减价'
               WHEN l.pm3 - f.pm3 <= -0.01 THEN N'客队深档减价'
               ELSE N'尾部未动'
           END AS TailTag
    FROM lr l
    JOIN fr f ON f.MatchId = l.MatchId
    LEFT JOIN deepdrop d ON d.MatchId = l.MatchId
    LEFT JOIN distj dj ON dj.MatchId = l.MatchId
) AS s
   ON t.MatchId = s.MatchId
WHEN MATCHED THEN UPDATE SET
     MatchNo = s.MatchNo, Rounds = s.Rounds, FirstAt = s.FirstAt, LastAt = s.LastAt,
     ExpGd = s.exp_gd, ExpGdOpen = s.exp_gd_open, ExpGdMove = s.exp_gd_move, ExpTotal = s.exp_tg,
     PHome = s.p_home, PDraw = s.p_draw, PAway = s.p_away, PHomeOpen = s.p_home_open,
     P3Plus = s.p3, PMinus3 = s.pm3, P3PlusMove = s.p3_move, PMinus3Move = s.pm3_move,
     DeepNegDrop = s.neg_drop, DeepPosDrop = s.pos_drop,
     DistText = s.DistText, TailTag = s.TailTag, UpdatedAt = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT
     (MatchId, MatchNo, Rounds, FirstAt, LastAt,
      ExpGd, ExpGdOpen, ExpGdMove, ExpTotal,
      PHome, PDraw, PAway, PHomeOpen,
      P3Plus, PMinus3, P3PlusMove, PMinus3Move,
      DeepNegDrop, DeepPosDrop, DistText, TailTag)
     VALUES (s.MatchId, s.MatchNo, s.Rounds, s.FirstAt, s.LastAt,
             s.exp_gd, s.exp_gd_open, s.exp_gd_move, s.exp_tg,
             s.p_home, s.p_draw, s.p_away, s.p_home_open,
             s.p3, s.pm3, s.p3_move, s.pm3_move,
             s.neg_drop, s.pos_drop, s.DistText, s.TailTag);
GO

SELECT COUNT(*) AS Matches,
       SUM(CASE WHEN Rounds >= 2 THEN 1 ELSE 0 END) AS MultiRound,
       SUM(CASE WHEN TailTag = N'尾部未动' THEN 1 ELSE 0 END) AS Unmoved,
       SUM(CASE WHEN DeepNegDrop + DeepPosDrop >= 3 THEN 1 ELSE 0 END) AS BatchDrop,
       CAST(AVG(ExpGd) AS DECIMAL(6,3)) AS AvgExpGd
FROM dbo.FbScoreDiag;
GO
