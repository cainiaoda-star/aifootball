-- 今日195场完场逐场明细：赔率变化 + 必发占比 + 信号 + 是否命中
WITH today AS (
  SELECT MatchId, HomeTeamName, AwayTeamName, LeagueName, HomeScore, AwayScore, ResultSpf, Handicap, TotalGoals
  FROM FbMatch WHERE Status=3 AND MatchTime>='2026-10-28' AND MatchTime<'2026-10-29'
),
o AS (SELECT MatchId, AVG(HomeWin) h, AVG(Draw) d, AVG(AwayWin) a FROM FbOdds WHERE OddsType='EU' AND IsOpening=1 AND CompanyId=0 GROUP BY MatchId),
c AS (SELECT MatchId, AVG(HomeWin) h, AVG(Draw) d, AVG(AwayWin) a FROM FbOdds WHERE OddsType='EU' AND IsOpening=0 AND CompanyId=0 GROUP BY MatchId),
ex AS (SELECT MatchId, RatioHome, RatioAway, TotalVolume FROM FbExchange),
ah_o AS (SELECT MatchId, AVG(Handicap) h FROM FbOdds WHERE OddsType='AH' AND IsOpening=1 AND CompanyId=0 GROUP BY MatchId),
ah_c AS (SELECT MatchId, AVG(Handicap) h FROM FbOdds WHERE OddsType='AH' AND IsOpening=0 AND CompanyId=0 GROUP BY MatchId)
SELECT
  t.LeagueName, t.HomeTeamName, t.AwayTeamName, t.HomeScore, t.AwayScore, t.ResultSpf,
  ROUND(o.h,2) AS openHome, ROUND(c.h,2) AS closeHome, ROUND(c.h-o.h,2) AS homeDelta,
  ex.RatioHome, ex.RatioAway,
  ROUND(ah_o.h,2) AS ahOpen, ROUND(ah_c.h,2) AS ahClose, ROUND(ah_c.h-ah_o.h,2) AS ahDelta
FROM today t
LEFT JOIN o ON t.MatchId=o.MatchId
LEFT JOIN c ON t.MatchId=c.MatchId
LEFT JOIN ex ON t.MatchId=ex.MatchId
LEFT JOIN ah_o ON t.MatchId=ah_o.MatchId
LEFT JOIN ah_c ON t.MatchId=ah_c.MatchId
ORDER BY t.MatchId
