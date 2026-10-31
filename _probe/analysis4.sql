WITH today AS (
  SELECT MatchId, ResultSpf, HomeTeamName, AwayTeamName FROM FbMatch
  WHERE Status=3 AND MatchTime >= '2026-10-28' AND MatchTime < '2026-10-29'
),
o AS (SELECT MatchId, AVG(HomeWin) h FROM FbOdds WHERE OddsType='EU' AND IsOpening=1 AND CompanyId=0 GROUP BY MatchId),
c AS (SELECT MatchId, AVG(HomeWin) h FROM FbOdds WHERE OddsType='EU' AND IsOpening=0 AND CompanyId=0 GROUP BY MatchId),
ex AS (SELECT MatchId, RatioHome, RatioAway FROM FbExchange WHERE MatchId IN (SELECT MatchId FROM today))
SELECT
  CASE
    WHEN (c.h-o.h) < -0.05 AND e.RatioHome >= 60 THEN '①赔率降+主胜大热'
    WHEN (c.h-o.h) < -0.05 AND e.RatioAway >= 60 THEN '②赔率降+客胜大热'
    WHEN (c.h-o.h) >  0.05 AND e.RatioHome >= 60 THEN '③赔率升+主胜大热'
    WHEN (c.h-o.h) >  0.05 AND e.RatioAway >= 60 THEN '④赔率升+客胜大热'
    ELSE '⑤其他'
  END AS signal,
  COUNT(*) AS n,
  SUM(CASE WHEN t.ResultSpf=3 THEN 1 ELSE 0 END) AS homeWin,
  SUM(CASE WHEN t.ResultSpf=0 THEN 1 ELSE 0 END) AS awayWin,
  SUM(CASE WHEN t.ResultSpf=1 THEN 1 ELSE 0 END) AS drawCnt,
  ROUND(100.0*SUM(CASE WHEN t.ResultSpf=3 THEN 1 ELSE 0 END)/COUNT(*),1) AS homePct,
  ROUND(100.0*SUM(CASE WHEN t.ResultSpf=0 THEN 1 ELSE 0 END)/COUNT(*),1) AS awayPct
FROM today t JOIN o ON t.MatchId=o.MatchId JOIN c ON t.MatchId=c.MatchId JOIN ex e ON t.MatchId=e.MatchId
GROUP BY CASE
    WHEN (c.h-o.h) < -0.05 AND e.RatioHome >= 60 THEN '①赔率降+主胜大热'
    WHEN (c.h-o.h) < -0.05 AND e.RatioAway >= 60 THEN '②赔率降+客胜大热'
    WHEN (c.h-o.h) >  0.05 AND e.RatioHome >= 60 THEN '③赔率升+主胜大热'
    WHEN (c.h-o.h) >  0.05 AND e.RatioAway >= 60 THEN '④赔率升+客胜大热'
    ELSE '⑤其他'
  END
ORDER BY n DESC
