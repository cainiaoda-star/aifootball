-- 完场样本上，均势条件下的真实平局率（用于校准推平阈值）
WITH eu AS (
  SELECT MatchId,
         MAX(CASE WHEN IsOpening = 1 THEN HomeWin END) AS OpenH,
         MAX(CASE WHEN IsOpening = 0 THEN HomeWin END) AS CloseH,
         MAX(CASE WHEN IsOpening = 1 THEN Draw END) AS OpenD,
         MAX(CASE WHEN IsOpening = 0 THEN Draw END) AS CloseD,
         MAX(CASE WHEN IsOpening = 1 THEN AwayWin END) AS OpenA,
         MAX(CASE WHEN IsOpening = 0 THEN AwayWin END) AS CloseA
  FROM dbo.FbOdds
  WHERE OddsType = 'EU' AND CompanyId = 0
  GROUP BY MatchId
),
ah AS (
  SELECT MatchId,
         MAX(CASE WHEN IsOpening = 0 THEN Handicap END) AS CloseHcp
  FROM dbo.FbOdds
  WHERE OddsType = 'AH' AND CompanyId = 0
  GROUP BY MatchId
),
ex AS (
  SELECT MatchId, RatioHome, RatioDraw, RatioAway
  FROM (
    SELECT MatchId, RatioHome, RatioDraw, RatioAway,
           ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt DESC) AS rn
    FROM dbo.FbExchange
  ) t WHERE rn = 1
),
base AS (
  SELECT f.MatchId, f.ResultSpf,
         eu.CloseH, eu.CloseD, eu.CloseA,
         eu.CloseH - eu.OpenH AS HomeDelta,
         eu.CloseD - eu.OpenD AS DrawDelta,
         ah.CloseHcp,
         ex.RatioHome, ex.RatioDraw, ex.RatioAway
  FROM dbo.FbMatch f
  JOIN eu ON eu.MatchId = f.MatchId
  LEFT JOIN ah ON ah.MatchId = f.MatchId
  LEFT JOIN ex ON ex.MatchId = f.MatchId
  WHERE f.Status = 3 AND f.ResultSpf IN ('3','1','0')
    AND eu.CloseH > 1 AND eu.CloseD > 1 AND eu.CloseA > 1
)
SELECT N'全部完场' AS Grp, COUNT(*) AS N,
       CAST(100.0 * SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END) / COUNT(*) AS DECIMAL(5,1)) AS DrawPct
FROM base
UNION ALL
SELECT N'均势规则(主客>=2/平<=3.4/盘<=0.5)', COUNT(*),
       CAST(100.0 * SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1))
FROM base
WHERE CloseH >= 2 AND CloseA >= 2 AND CloseD <= 3.4
  AND (CloseHcp IS NULL OR ABS(CloseHcp) <= 0.5)
UNION ALL
SELECT N'均势+主胜升降|d|<=0.05', COUNT(*),
       CAST(100.0 * SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1))
FROM base
WHERE CloseH >= 2 AND CloseA >= 2 AND CloseD <= 3.4
  AND (CloseHcp IS NULL OR ABS(CloseHcp) <= 0.5)
  AND HomeDelta IS NOT NULL AND ABS(HomeDelta) <= 0.05
UNION ALL
SELECT N'均势+平赔下降(<=-0.05)', COUNT(*),
       CAST(100.0 * SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1))
FROM base
WHERE CloseH >= 2 AND CloseA >= 2 AND CloseD <= 3.4
  AND (CloseHcp IS NULL OR ABS(CloseHcp) <= 0.5)
  AND DrawDelta IS NOT NULL AND DrawDelta <= -0.05
UNION ALL
SELECT N'均势+必发平>=35%', COUNT(*),
       CAST(100.0 * SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1))
FROM base
WHERE CloseH >= 2 AND CloseA >= 2 AND CloseD <= 3.4
  AND (CloseHcp IS NULL OR ABS(CloseHcp) <= 0.5)
  AND RatioDraw IS NOT NULL AND RatioDraw >= 35
  AND (RatioHome IS NULL OR RatioHome < 60)
  AND (RatioAway IS NULL OR RatioAway < 60)
UNION ALL
SELECT N'均势+必发平>=40%', COUNT(*),
       CAST(100.0 * SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1))
FROM base
WHERE CloseH >= 2 AND CloseA >= 2 AND CloseD <= 3.4
  AND (CloseHcp IS NULL OR ABS(CloseHcp) <= 0.5)
  AND RatioDraw IS NOT NULL AND RatioDraw >= 40
  AND (RatioHome IS NULL OR RatioHome < 60)
  AND (RatioAway IS NULL OR RatioAway < 60)
UNION ALL
SELECT N'均势+必发平>=30%+平赔降', COUNT(*),
       CAST(100.0 * SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1))
FROM base
WHERE CloseH >= 2 AND CloseA >= 2 AND CloseD <= 3.4
  AND (CloseHcp IS NULL OR ABS(CloseHcp) <= 0.5)
  AND RatioDraw IS NOT NULL AND RatioDraw >= 30
  AND DrawDelta IS NOT NULL AND DrawDelta <= -0.05
  AND (RatioHome IS NULL OR RatioHome < 60)
  AND (RatioAway IS NULL OR RatioAway < 60)
UNION ALL
SELECT N'均势+去水平最高', COUNT(*),
       CAST(100.0 * SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1))
FROM base
WHERE CloseH >= 2 AND CloseA >= 2 AND CloseD <= 3.4
  AND (CloseHcp IS NULL OR ABS(CloseHcp) <= 0.5)
  AND (1.0/CloseD) >= (1.0/CloseH) AND (1.0/CloseD) >= (1.0/CloseA)
ORDER BY N DESC;
