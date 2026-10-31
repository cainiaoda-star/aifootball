-- 今天完场比赛：欧赔初盘→终盘 主胜赔率变化 vs 实际赛果
WITH today AS (
  SELECT MatchId, ResultSpf, HomeScore, AwayScore
  FROM FbMatch
  WHERE Status=3 AND MatchTime >= '2026-10-28' AND MatchTime < '2026-10-29'
),
open_avg AS (
  SELECT MatchId, AVG(HomeWin) AS openHome, AVG(Draw) AS openDraw, AVG(AwayWin) AS openAway
  FROM FbOdds WHERE OddsType='EU' AND IsOpening=1 AND CompanyId=0
  GROUP BY MatchId
),
close_avg AS (
  SELECT MatchId, AVG(HomeWin) AS closeHome, AVG(Draw) AS closeDraw, AVG(AwayWin) AS closeAway
  FROM FbOdds WHERE OddsType='EU' AND IsOpening=0 AND CompanyId=0
  GROUP BY MatchId
)
SELECT 
  t.MatchId, t.HomeScore, t.AwayScore, t.ResultSpf,
  ROUND(o.openHome,2) AS openHome, ROUND(c.closeHome,2) AS closeHome,
  ROUND(c.closeHome - o.openHome, 2) AS homeDelta,
  ROUND(o.openAway,2) AS openAway, ROUND(c.closeAway,2) AS closeAway,
  ROUND(c.closeAway - o.openAway, 2) AS awayDelta
FROM today t
JOIN open_avg o ON t.MatchId = o.MatchId
JOIN close_avg c ON t.MatchId = c.MatchId
ORDER BY homeDelta
