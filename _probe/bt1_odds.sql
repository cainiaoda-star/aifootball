WITH base AS (
  SELECT m.MatchId, m.ResultSpf, c.HomeWin - o.HomeWin AS homeDelta
  FROM dbo.FbMatch m
  JOIN dbo.FbOdds o ON o.MatchId=m.MatchId AND o.OddsType='EU' AND o.IsOpening=1 AND o.CompanyId=0
  JOIN dbo.FbOdds c ON c.MatchId=m.MatchId AND c.OddsType='EU' AND c.IsOpening=0 AND c.CompanyId=0
  WHERE m.Status=3 AND m.ResultSpf IS NOT NULL AND o.HomeWin IS NOT NULL AND c.HomeWin IS NOT NULL
)
SELECT
  CASE
    WHEN homeDelta <= -0.20 THEN 'A_big_drop<=-0.20'
    WHEN homeDelta <  -0.05 THEN 'B_small_drop_-0.05~-0.20'
    WHEN homeDelta BETWEEN -0.05 AND 0.05 THEN 'C_flat'
    WHEN homeDelta <= 0.20 THEN 'D_small_rise_0.05~0.20'
    ELSE 'E_big_rise_>0.20'
  END AS grp,
  COUNT(*) n,
  SUM(CASE WHEN ResultSpf='3' THEN 1 ELSE 0 END) homeWin,
  ROUND(100.0*SUM(CASE WHEN ResultSpf='3' THEN 1 ELSE 0 END)/COUNT(*),1) homePct,
  SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END) drawCnt,
  ROUND(100.0*SUM(CASE WHEN ResultSpf='1' THEN 1 ELSE 0 END)/COUNT(*),1) drawPct,
  SUM(CASE WHEN ResultSpf='0' THEN 1 ELSE 0 END) awayWin,
  ROUND(100.0*SUM(CASE WHEN ResultSpf='0' THEN 1 ELSE 0 END)/COUNT(*),1) awayPct
FROM base
GROUP BY CASE
    WHEN homeDelta <= -0.20 THEN 'A_big_drop<=-0.20'
    WHEN homeDelta <  -0.05 THEN 'B_small_drop_-0.05~-0.20'
    WHEN homeDelta BETWEEN -0.05 AND 0.05 THEN 'C_flat'
    WHEN homeDelta <= 0.20 THEN 'D_small_rise_0.05~0.20'
    ELSE 'E_big_rise_>0.20'
  END
ORDER BY grp
