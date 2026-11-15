-- 竞彩大奶策略审查用的只读汇总。不改表。
-- 跑法：tools\db.ps1 -Query (Get-Content 本文件 -Raw)
-- 46 的定义：受注日 + 场次 + 玩法 + 档位，三个来源取并集后去重。
--   1) FbMilkPick 没有 RunId，且 CreatedAt 早于开赛
--   2) 成功轮 FbMilkRun.Status = success，且该档最早 StartedAt 早于开赛
--   3) FbPaperTrade.FrozenAt 早于开赛
-- 命中、赔率、概率用库里当时写下的数。不重跑今天的模型。
-- 假想平注：中了记赔率-1，未中记 -1，撤注、取消、未完场记 0。不是真实投注。

WITH milk_legacy AS (
    SELECT p.PickDate, p.MatchId, p.PlayType, p.OptionCode
    FROM dbo.FbMilkPick p
    WHERE p.RunId IS NULL AND p.CreatedAt < p.MatchTime
),
success_one AS (
    SELECT p.PickDate, p.MatchId, p.PlayType, p.OptionCode
    FROM dbo.FbMilkPick p
    JOIN dbo.FbMilkRunDate rd ON rd.RunId = p.RunId AND rd.SalesDate = p.PickDate
    JOIN dbo.FbMilkRun r ON r.RunId = p.RunId AND r.Status = N'success'
    WHERE r.StartedAt < p.MatchTime
    GROUP BY p.PickDate, p.MatchId, p.PlayType, p.OptionCode
),
paper AS (
    SELECT t.TradeDate AS PickDate, t.MatchId, t.PlayType, t.OptionCode
    FROM dbo.FbPaperTrade t
    WHERE t.FrozenAt < t.MatchTime
),
dedup AS (
    SELECT PickDate, MatchId, PlayType, OptionCode FROM milk_legacy
    UNION
    SELECT PickDate, MatchId, PlayType, OptionCode FROM success_one
    UNION
    SELECT PickDate, MatchId, PlayType, OptionCode FROM paper
),
filled AS (
    SELECT d.PickDate, d.MatchId, d.PlayType, d.OptionCode,
           COALESCE(p.Odds, t.Odds) AS Odds,
           COALESCE(p.ModelProbability, t.ModelProbability) AS ModelPct,
           COALESCE(p.Hit, t.Hit) AS Hit,
           CASE WHEN ISNULL(p.Voided, 0) = 1 OR ISNULL(t.Voided, 0) = 1 THEN 1 ELSE 0 END AS Voided,
           COALESCE(p.ActualScore, t.ActualScore) AS ActualScore
    FROM dedup d
    LEFT JOIN dbo.FbMilkPick p
      ON p.PickDate = d.PickDate AND p.MatchId = d.MatchId
     AND p.PlayType = d.PlayType AND p.OptionCode = d.OptionCode
     AND p.PickId = (
          SELECT MIN(x.PickId) FROM dbo.FbMilkPick x
          WHERE x.PickDate = d.PickDate AND x.MatchId = d.MatchId
            AND x.PlayType = d.PlayType AND x.OptionCode = d.OptionCode)
    LEFT JOIN dbo.FbPaperTrade t
      ON t.TradeDate = d.PickDate AND t.MatchId = d.MatchId
     AND t.PlayType = d.PlayType AND t.OptionCode = d.OptionCode
)
SELECT PlayType,
       COUNT(*) AS Keys,
       SUM(CASE WHEN Hit = 1 AND Voided = 0 AND ISNULL(ActualScore, N'') <> N'取消' THEN 1 ELSE 0 END) AS Hits,
       SUM(CASE WHEN Hit = 0 AND Voided = 0 AND ISNULL(ActualScore, N'') <> N'取消' THEN 1 ELSE 0 END) AS Miss,
       SUM(CASE WHEN Voided = 1 THEN 1 ELSE 0 END) AS Voided,
       SUM(CASE WHEN ISNULL(ActualScore, N'') = N'取消' THEN 1 ELSE 0 END) AS Cancelled,
       SUM(CASE WHEN Hit IS NULL AND Voided = 0 AND ISNULL(ActualScore, N'') <> N'取消' THEN 1 ELSE 0 END) AS OpenRows,
       CAST(SUM(CASE WHEN Hit = 1 AND Voided = 0 AND ISNULL(ActualScore, N'') <> N'取消' THEN Odds - 1
                     WHEN Hit = 0 AND Voided = 0 AND ISNULL(ActualScore, N'') <> N'取消' THEN -1
                     ELSE 0 END) AS decimal(10, 2)) AS FlatPnl,
       CAST(SUM(CASE WHEN Hit IS NOT NULL AND Voided = 0 AND ISNULL(ActualScore, N'') <> N'取消'
                     THEN ModelPct ELSE 0 END) AS decimal(10, 2)) AS SumModelPct
FROM filled
GROUP BY PlayType
ORDER BY PlayType;

-- 逐场跟进账单独算，不要和上面的结果相加。
SELECT PlayType,
       COUNT(*) AS N,
       SUM(CASE WHEN Hit = 1 THEN 1 ELSE 0 END) AS Hits,
       SUM(CASE WHEN Hit = 0 THEN 1 ELSE 0 END) AS Miss,
       SUM(CASE WHEN Hit IS NULL THEN 1 ELSE 0 END) AS OpenRows,
       CAST(SUM(CASE WHEN Hit = 1 THEN Odds - 1 WHEN Hit = 0 THEN -1.0 ELSE 0 END) AS decimal(10, 2)) AS FlatPnl,
       CAST(SUM(CASE WHEN Hit IS NOT NULL THEN ModelProbability ELSE 0 END) AS decimal(10, 2)) AS SumModelPct
FROM dbo.FbDailyFollow
GROUP BY PlayType
ORDER BY PlayType;
