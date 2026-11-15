-- 只读。核对 2026-11-15 总进球条件频率实验的对照公平性。
-- 不改分档、不改 12 场门槛、不写库、不用终赔填初赔。
-- 执行：tools\db.ps1 -Query (Get-Content -Raw 本文件) 会把多批结果连在一起；
-- 下面各批已按 2026-11-15 当晚在 AiFootBall 上跑过，结果写在同名 md 的「对照公平」一节。

-- 1. 22.31% 的可复核范围。注释写的是 2023-08 起 269091 场（2026-11-08 写入）。
--    现库已对不上 269091。下面这刀的 2 球比例仍是 22.31%，且包含 2026-09-01 之后。
SELECT 'snapshot-before-2026-11-08' AS Cut,
       COUNT(*) AS N,
       SUM(CASE WHEN HomeScore + AwayScore = 2 THEN 1 ELSE 0 END) AS TwoGoals,
       CAST(100.0 * SUM(CASE WHEN HomeScore + AwayScore = 2 THEN 1 ELSE 0 END) / COUNT(*) AS decimal(10,2)) AS Pct,
       SUM(CASE WHEN MatchTime >= '2026-09-01' THEN 1 ELSE 0 END) AS OnOrAfterValidStart
FROM dbo.FbMatch
WHERE MatchTime >= '2023-08-01' AND MatchTime < '2026-11-08'
  AND HomeScore IS NOT NULL AND AwayScore IS NOT NULL;

SELECT 'before-valid-start' AS Cut,
       COUNT(*) AS N,
       SUM(CASE WHEN HomeScore + AwayScore = 2 THEN 1 ELSE 0 END) AS TwoGoals,
       CAST(100.0 * SUM(CASE WHEN HomeScore + AwayScore = 2 THEN 1 ELSE 0 END) / COUNT(*) AS decimal(10,2)) AS Pct
FROM dbo.FbMatch
WHERE MatchTime >= '2023-08-01' AND MatchTime < '2026-09-01'
  AND HomeScore IS NOT NULL AND AwayScore IS NOT NULL;

-- 2. 训练期全局 2 球频率，以及与条件频率在同一批覆盖场上的 Brier。
--    训练：赛果可知时刻（开赛 + 150 分钟）<= 2026-09-01，且有平均值欧赔初赔。
--    常数只用这一刀，不看验证期命中再改。
WITH one_odds AS (
  SELECT MatchId, MIN(HomeWin) AS HomeWin, MIN(CapturedAt) AS FirstWrite
  FROM dbo.FbOdds
  WHERE Company = N'平均值' AND OddsType = N'EU' AND IsOpening = 1 AND HomeWin > 0
  GROUP BY MatchId
),
priced AS (
  SELECT m.MatchId, m.LeagueName, m.MatchTime, o.FirstWrite,
         CASE WHEN o.HomeWin < 1.60 THEN 0 WHEN o.HomeWin < 1.90 THEN 1
              WHEN o.HomeWin < 2.20 THEN 2 WHEN o.HomeWin < 2.60 THEN 3 ELSE 4 END AS Band,
         CASE WHEN m.HomeScore + m.AwayScore = 2 THEN CAST(1 AS decimal(18,6)) ELSE CAST(0 AS decimal(18,6)) END AS IsTwo,
         CASE WHEN DATEADD(minute, 150, m.MatchTime) <= '2026-09-01' THEN 1 ELSE 0 END AS InTrain,
         CASE WHEN m.MatchTime >= '2026-09-01' AND m.MatchTime < '2026-11-01' THEN 1 ELSE 0 END AS InValid
  FROM dbo.FbMatch m
  JOIN one_odds o ON o.MatchId = m.MatchId
  WHERE m.HomeScore IS NOT NULL AND m.AwayScore >= 0 AND m.MatchTime < '2026-11-01'
),
cells AS (
  SELECT LeagueName, Band, COUNT(*) AS N, SUM(IsTwo) AS TwoGoals
  FROM priced WHERE InTrain = 1
  GROUP BY LeagueName, Band
),
covered AS (
  SELECT v.MatchId, v.IsTwo, v.FirstWrite, v.MatchTime,
         CAST(c.TwoGoals AS decimal(18,8)) / c.N AS PCond
  FROM priced v
  JOIN cells c ON c.LeagueName = v.LeagueName AND c.Band = v.Band
  WHERE v.InValid = 1 AND c.N >= 12
),
train AS (
  SELECT CAST(SUM(IsTwo) AS decimal(18,8)) / COUNT(*) AS P,
         COUNT(*) AS N, SUM(IsTwo) AS TwoGoals
  FROM priced WHERE InTrain = 1
)
SELECT
  COUNT(*) AS CoveredRows,
  COUNT(DISTINCT c.MatchId) AS CoveredMatches,
  MIN(c.MatchId) AS MinId,
  MAX(c.MatchId) AS MaxId,
  SUM(CAST(c.MatchId AS bigint)) AS SumId,
  CHECKSUM_AGG(CHECKSUM(c.MatchId)) AS IdChecksum,
  SUM(c.IsTwo) AS ActualTwos,
  CAST(SUM(c.IsTwo) / COUNT(*) AS decimal(12,6)) AS ActualP,
  MAX(t.N) AS TrainN,
  MAX(t.TwoGoals) AS TrainTwos,
  CAST(MAX(t.P) AS decimal(12,6)) AS TrainP,
  CAST(SUM(POWER(c.PCond - c.IsTwo, 2)) / COUNT(*) AS decimal(12,6)) AS CondBrier,
  CAST(SUM(POWER(t.P - c.IsTwo, 2)) / COUNT(*) AS decimal(12,6)) AS TrainBrier,
  CAST(SUM(POWER(CAST(0.2231 AS float) - c.IsTwo, 2)) / COUNT(*) AS decimal(12,6)) AS Const2231Brier,
  CAST(SUM(POWER(c.PCond - c.IsTwo, 2)) / COUNT(*)
       - SUM(POWER(t.P - c.IsTwo, 2)) / COUNT(*) AS decimal(12,6)) AS CondMinusTrain,
  SUM(CASE WHEN c.FirstWrite <= c.MatchTime THEN 1 ELSE 0 END) AS OpenWrittenBeforeKick
FROM covered c
CROSS JOIN train t;

-- 3. 6112 场里的覆盖与拒答。拒答不计入 Brier，也不算预测正确。
WITH one_odds AS (
  SELECT MatchId, MIN(HomeWin) AS HomeWin
  FROM dbo.FbOdds
  WHERE Company = N'平均值' AND OddsType = N'EU' AND IsOpening = 1 AND HomeWin > 0
  GROUP BY MatchId
),
priced AS (
  SELECT m.MatchId, m.LeagueName,
         CASE WHEN o.HomeWin < 1.60 THEN 0 WHEN o.HomeWin < 1.90 THEN 1
              WHEN o.HomeWin < 2.20 THEN 2 WHEN o.HomeWin < 2.60 THEN 3 ELSE 4 END AS Band,
         CASE WHEN m.HomeScore + m.AwayScore = 2 THEN 1.0 ELSE 0.0 END AS IsTwo,
         CASE WHEN DATEADD(minute, 150, m.MatchTime) <= '2026-09-01' THEN 1 ELSE 0 END AS InTrain,
         CASE WHEN m.MatchTime >= '2026-09-01' AND m.MatchTime < '2026-11-01' THEN 1 ELSE 0 END AS InValid
  FROM dbo.FbMatch m
  JOIN one_odds o ON o.MatchId = m.MatchId
  WHERE m.HomeScore IS NOT NULL AND m.AwayScore >= 0 AND m.MatchTime < '2026-11-01'
),
cells AS (
  SELECT LeagueName, Band, COUNT(*) AS N
  FROM priced WHERE InTrain = 1
  GROUP BY LeagueName, Band
)
SELECT
  SUM(v.InValid) AS ValidN,
  SUM(CASE WHEN v.InValid = 1 AND c.N >= 12 THEN 1 ELSE 0 END) AS Covered,
  SUM(CASE WHEN v.InValid = 1 AND (c.N IS NULL OR c.N < 12) THEN 1 ELSE 0 END) AS Abstain,
  SUM(CASE WHEN v.InValid = 1 THEN v.IsTwo ELSE 0 END) AS ValidTwos,
  SUM(CASE WHEN v.InValid = 1 AND (c.N IS NULL OR c.N < 12) THEN v.IsTwo ELSE 0 END) AS AbstainTwos
FROM priced v
LEFT JOIN cells c ON c.LeagueName = v.LeagueName AND c.Band = v.Band;

-- 4. 初赔写入时刻。CapturedAt 是行最后一次写入时间，不是赔率发布时间。
--    这里只计数，不用终赔或历史即时盘替换分档。
WITH one_odds AS (
  SELECT MatchId, MIN(HomeWin) AS HomeWin, MIN(CapturedAt) AS FirstWrite
  FROM dbo.FbOdds
  WHERE Company = N'平均值' AND OddsType = N'EU' AND IsOpening = 1 AND HomeWin > 0
  GROUP BY MatchId
),
valid AS (
  SELECT m.MatchId, m.MatchTime, o.HomeWin, o.FirstWrite
  FROM dbo.FbMatch m
  JOIN one_odds o ON o.MatchId = m.MatchId
  WHERE m.HomeScore IS NOT NULL AND m.AwayScore >= 0
    AND m.MatchTime >= '2026-09-01' AND m.MatchTime < '2026-11-01'
)
SELECT
  COUNT(*) AS ValidWithOpen,
  SUM(CASE WHEN FirstWrite <= MatchTime THEN 1 ELSE 0 END) AS WriteOnOrBeforeKick,
  SUM(CASE WHEN FirstWrite > MatchTime THEN 1 ELSE 0 END) AS WriteAfterKick,
  CONVERT(varchar(19), MIN(FirstWrite), 120) AS MinWrite,
  CONVERT(varchar(19), MAX(FirstWrite), 120) AS MaxWrite
FROM valid;
