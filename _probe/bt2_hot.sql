WITH fin AS (
  SELECT MatchId, ResultSpf FROM dbo.FbMatch WHERE Status=3 AND ResultSpf IS NOT NULL
),
ex AS (
  SELECT e.MatchId, e.RatioHome, e.RatioAway, e.RatioDraw,
         ROW_NUMBER() OVER (PARTITION BY e.MatchId ORDER BY e.CapturedAt DESC) rn
  FROM dbo.FbExchange e
  WHERE e.MatchId IN (SELECT MatchId FROM fin)
),
x AS (
  SELECT f.ResultSpf, e.RatioHome, e.RatioAway
  FROM fin f JOIN ex e ON f.MatchId=e.MatchId AND e.rn=1
  WHERE e.RatioHome IS NOT NULL
)
SELECT
  CASE
    WHEN RatioHome >= 90 THEN 'H90+'
    WHEN RatioHome >= 75 THEN 'H75-90'
    WHEN RatioHome >= 60 THEN 'H60-75'
    WHEN RatioHome >= 45 THEN 'H45-60'
    WHEN RatioAway >= 90 THEN 'A90+'
    WHEN RatioAway >= 75 THEN 'A75-90'
    WHEN RatioAway >= 60 THEN 'A60-75'
    WHEN RatioAway >= 45 THEN 'A45-60'
    ELSE 'balanced'
  END AS grp,
  COUNT(*) n,
  SUM(CASE WHEN ResultSpf='3' THEN 1 ELSE 0 END) homeWin,
  ROUND(100.0*SUM(CASE WHEN ResultSpf='3' THEN 1 ELSE 0 END)/COUNT(*),1) homePct,
  SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END) drawCnt,
  ROUND(100.0*SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END)/COUNT(*),1) drawPct,
  SUM(CASE WHEN ResultSpf='0' THEN 1 ELSE 0 END) awayWin,
  ROUND(100.0*SUM(CASE WHEN ResultSpf='0' THEN 1 ELSE 0 END)/COUNT(*),1) awayPct
FROM x
GROUP BY CASE
    WHEN RatioHome >= 90 THEN 'H90+'
    WHEN RatioHome >= 75 THEN 'H75-90'
    WHEN RatioHome >= 60 THEN 'H60-75'
    WHEN RatioHome >= 45 THEN 'H45-60'
    WHEN RatioAway >= 90 THEN 'A90+'
    WHEN RatioAway >= 75 THEN 'A75-90'
    WHEN RatioAway >= 60 THEN 'A60-75'
    WHEN RatioAway >= 45 THEN 'A45-60'
    ELSE 'balanced'
  END
ORDER BY grp
