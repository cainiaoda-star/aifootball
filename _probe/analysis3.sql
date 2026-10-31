WITH today AS (
  SELECT MatchId, ResultSpf, HomeTeamName, AwayTeamName FROM FbMatch
  WHERE Status=3 AND MatchTime >= '2026-10-28' AND MatchTime < '2026-10-29'
),
ex AS (
  SELECT MatchId, RatioHome, RatioDraw, RatioAway, TotalVolume, PnlHome, PnlAway, TipLean
  FROM FbExchange
  WHERE MatchId IN (SELECT MatchId FROM today)
)
SELECT
  CASE
    WHEN e.RatioHome >= 60 THEN '主胜投注占比>=60%(大热)'
    WHEN e.RatioHome >= 45 THEN '主胜投注占比45~60%'
    WHEN e.RatioAway >= 60 THEN '客胜投注占比>=60%(大热)'
    WHEN e.RatioAway >= 45 THEN '客胜投注占比45~60%'
    ELSE '主客投注相对均衡'
  END AS grp,
  COUNT(*) AS n,
  SUM(CASE WHEN t.ResultSpf=3 THEN 1 ELSE 0 END) AS homeWin,
  ROUND(100.0*SUM(CASE WHEN t.ResultSpf=3 THEN 1 ELSE 0 END)/COUNT(*),1) AS homeWinPct,
  SUM(CASE WHEN t.ResultSpf=0 THEN 1 ELSE 0 END) AS awayWin,
  ROUND(100.0*SUM(CASE WHEN t.ResultSpf=0 THEN 1 ELSE 0 END)/COUNT(*),1) AS awayWinPct,
  ROUND(AVG(e.TotalVolume),0) AS avgVol
FROM today t JOIN ex e ON t.MatchId=e.MatchId
GROUP BY CASE
    WHEN e.RatioHome >= 60 THEN '主胜投注占比>=60%(大热)'
    WHEN e.RatioHome >= 45 THEN '主胜投注占比45~60%'
    WHEN e.RatioAway >= 60 THEN '客胜投注占比>=60%(大热)'
    WHEN e.RatioAway >= 45 THEN '客胜投注占比45~60%'
    ELSE '主客投注相对均衡'
  END
ORDER BY n DESC
