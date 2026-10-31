-- 深挖2：大小球盘口 vs 实际进球
WITH today AS (
  SELECT MatchId, HomeScore, AwayScore FROM FbMatch
  WHERE Status=3 AND MatchTime>='2026-10-28' AND MatchTime<'2026-10-29'
),
ou AS (
  SELECT MatchId, AVG(TotalLine) tl FROM FbOdds WHERE OddsType='OU' AND IsOpening=0 AND CompanyId=0 GROUP BY MatchId
)
SELECT
  CASE WHEN o.tl < 2.5 THEN '小2.5' WHEN o.tl = 2.5 THEN '2.5球' WHEN o.tl > 2.5 THEN '大2.5+' ELSE '无' END grp,
  COUNT(*) n,
  AVG(t.HomeScore+t.AwayScore) avgGoals,
  SUM(CASE WHEN t.HomeScore+t.AwayScore >= 3 THEN 1 ELSE 0 END) over25,
  ROUND(100.0*SUM(CASE WHEN t.HomeScore+t.AwayScore >= 3 THEN 1 ELSE 0 END)/COUNT(*),1) over25Pct
FROM today t JOIN ou o ON t.MatchId=o.MatchId
GROUP BY CASE WHEN o.tl < 2.5 THEN '小2.5' WHEN o.tl = 2.5 THEN '2.5球' WHEN o.tl > 2.5 THEN '大2.5+' ELSE '无' END
ORDER BY n DESC
