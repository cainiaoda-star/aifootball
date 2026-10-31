WITH eu AS (
  SELECT MatchId,
         MAX(CASE WHEN IsOpening = 1 THEN HomeWin END) AS OpenH,
         MAX(CASE WHEN IsOpening = 0 THEN HomeWin END) AS CloseH,
         MAX(CASE WHEN IsOpening = 1 THEN Draw END) AS OpenD,
         MAX(CASE WHEN IsOpening = 0 THEN Draw END) AS CloseD,
         MAX(CASE WHEN IsOpening = 0 THEN AwayWin END) AS CloseA
  FROM dbo.FbOdds WHERE OddsType='EU' AND CompanyId=0 GROUP BY MatchId
),
ah AS (
  SELECT MatchId, MAX(CASE WHEN IsOpening=0 THEN Handicap END) AS CloseHcp
  FROM dbo.FbOdds WHERE OddsType='AH' AND CompanyId=0 GROUP BY MatchId
),
ex AS (
  SELECT MatchId, RatioHome, RatioDraw, RatioAway FROM (
    SELECT MatchId, RatioHome, RatioDraw, RatioAway,
           ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt DESC) AS rn
    FROM dbo.FbExchange) t WHERE rn=1
),
base AS (
  SELECT f.ResultSpf, eu.CloseH, eu.CloseD, eu.CloseA,
         eu.CloseD-eu.OpenD AS DrawDelta, eu.CloseH-eu.OpenH AS HomeDelta,
         ah.CloseHcp, ex.RatioHome, ex.RatioDraw, ex.RatioAway
  FROM dbo.FbMatch f
  JOIN eu ON eu.MatchId=f.MatchId
  LEFT JOIN ah ON ah.MatchId=f.MatchId
  LEFT JOIN ex ON ex.MatchId=f.MatchId
  WHERE f.Status=3 AND f.ResultSpf IN ('3','1','0')
    AND eu.CloseH>1 AND eu.CloseD>1 AND eu.CloseA>1
),
open_game AS (
  SELECT * FROM base
  WHERE CloseH>=2 AND CloseA>=2 AND CloseD<=3.4
    AND (CloseHcp IS NULL OR ABS(CloseHcp)<=0.5)
)
SELECT N'均势+平赔降+无主客热' AS Grp, COUNT(*) N,
  CAST(100.0*SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)) DrawPct
FROM open_game
WHERE DrawDelta IS NOT NULL AND DrawDelta<=-0.05
  AND (RatioHome IS NULL OR RatioHome<60) AND (RatioAway IS NULL OR RatioAway<60)
UNION ALL
SELECT N'均势+平赔降>=0.08', COUNT(*),
  CAST(100.0*SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1))
FROM open_game WHERE DrawDelta IS NOT NULL AND DrawDelta<=-0.08
UNION ALL
SELECT N'均势+必发平领先且>=25', COUNT(*),
  CAST(100.0*SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1))
FROM open_game
WHERE RatioDraw IS NOT NULL AND RatioDraw>=25
  AND RatioDraw>=ISNULL(RatioHome,0) AND RatioDraw>=ISNULL(RatioAway,0)
UNION ALL
SELECT N'均势+必发平领先+平赔降', COUNT(*),
  CAST(100.0*SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1))
FROM open_game
WHERE RatioDraw IS NOT NULL AND RatioDraw>=25
  AND RatioDraw>=ISNULL(RatioHome,0) AND RatioDraw>=ISNULL(RatioAway,0)
  AND DrawDelta IS NOT NULL AND DrawDelta<=-0.05
UNION ALL
SELECT N'均势+CloseD<=3.2', COUNT(*),
  CAST(100.0*SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1))
FROM open_game WHERE CloseD<=3.2
UNION ALL
SELECT N'均势+CloseD<=3.0', COUNT(*),
  CAST(100.0*SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1))
FROM open_game WHERE CloseD<=3.0
ORDER BY N DESC;
