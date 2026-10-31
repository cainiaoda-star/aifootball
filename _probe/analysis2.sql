WITH today AS (
  SELECT MatchId, ResultSpf FROM FbMatch
  WHERE Status=3 AND MatchTime >= '2026-10-28' AND MatchTime < '2026-10-29'
),
o AS (SELECT MatchId, AVG(HomeWin) h, AVG(Draw) d, AVG(AwayWin) a FROM FbOdds WHERE OddsType='EU' AND IsOpening=1 AND CompanyId=0 GROUP BY MatchId),
c AS (SELECT MatchId, AVG(HomeWin) h, AVG(Draw) d, AVG(AwayWin) a FROM FbOdds WHERE OddsType='EU' AND IsOpening=0 AND CompanyId=0 GROUP BY MatchId)
SELECT
  CASE
    WHEN c.h - o.h <= -0.20 THEN '主胜赔率大降(<=-0.20)'
    WHEN c.h - o.h <  -0.05 THEN '主胜赔率小降(-0.05~-0.20)'
    WHEN c.h - o.h BETWEEN -0.05 AND 0.05 THEN '主胜赔率持平'
    WHEN c.h - o.h >  0.05 THEN '主胜赔率上升(>0.05)'
  END AS grp,
  COUNT(*) AS n,
  SUM(CASE WHEN ResultSpf=3 THEN 1 ELSE 0 END) AS homeWin,
  ROUND(100.0*SUM(CASE WHEN ResultSpf=3 THEN 1 ELSE 0 END)/COUNT(*),1) AS homeWinPct,
  SUM(CASE WHEN ResultSpf=0 THEN 1 ELSE 0 END) AS awayWin,
  ROUND(100.0*SUM(CASE WHEN ResultSpf=0 THEN 1 ELSE 0 END)/COUNT(*),1) AS awayWinPct,
  SUM(CASE WHEN ResultSpf=1 THEN 1 ELSE 0 END) AS drawCnt
FROM today t JOIN o ON t.MatchId=o.MatchId JOIN c ON t.MatchId=c.MatchId
GROUP BY CASE
    WHEN c.h - o.h <= -0.20 THEN '主胜赔率大降(<=-0.20)'
    WHEN c.h - o.h <  -0.05 THEN '主胜赔率小降(-0.05~-0.20)'
    WHEN c.h - o.h BETWEEN -0.05 AND 0.05 THEN '主胜赔率持平'
    WHEN c.h - o.h >  0.05 THEN '主胜赔率上升(>0.05)'
  END
ORDER BY MIN(c.h - o.h)
