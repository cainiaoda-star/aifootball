-- 深挖1：亚盘让球方向 vs 赛果（主让负值=主队被看好）
WITH today AS (
  SELECT MatchId, ResultSpf, HomeScore, AwayScore FROM FbMatch
  WHERE Status=3 AND MatchTime>='2026-10-28' AND MatchTime<'2026-10-29'
),
ah AS (
  SELECT MatchId, AVG(Handicap) h FROM FbOdds WHERE OddsType='AH' AND IsOpening=0 AND CompanyId=0 GROUP BY MatchId
)
SELECT
  CASE
    WHEN a.h <= -0.75 THEN '主让0.75+（主队强）'
    WHEN a.h < 0 THEN '主让0.25-0.5'
    WHEN a.h = 0 THEN '平手盘'
    WHEN a.h > 0 THEN '客让（主队受让）'
    ELSE '无盘'
  END AS grp,
  COUNT(*) n,
  SUM(CASE WHEN t.ResultSpf=3 THEN 1 ELSE 0 END) homeWin,
  ROUND(100.0*SUM(CASE WHEN t.ResultSpf=3 THEN 1 ELSE 0 END)/COUNT(*),1) homePct,
  SUM(CASE WHEN t.ResultSpf=0 THEN 1 ELSE 0 END) awayWin,
  ROUND(100.0*SUM(CASE WHEN t.ResultSpf=0 THEN 1 ELSE 0 END)/COUNT(*),1) awayPct
FROM today t JOIN ah a ON t.MatchId=a.MatchId
GROUP BY CASE
    WHEN a.h <= -0.75 THEN '主让0.75+（主队强）'
    WHEN a.h < 0 THEN '主让0.25-0.5'
    WHEN a.h = 0 THEN '平手盘'
    WHEN a.h > 0 THEN '客让（主队受让）'
    ELSE '无盘'
  END
ORDER BY n DESC
