SELECT c.MatchNo, c.MatchId, c.HomeTeam, c.AwayTeam, c.MatchTime,
       m.OddsSampledAt, m.OddsCompanyCount, m.ExchangeCheckedAt, m.ExchangeStatus,
       CASE WHEN o.MatchId IS NULL THEN 0 ELSE 1 END AS hasEu,
       CASE WHEN e.MatchId IS NULL THEN 0 ELSE 1 END AS hasEx
FROM dbo.BettingCard c
LEFT JOIN dbo.FbMatch m ON m.MatchId = c.MatchId
LEFT JOIN (
  SELECT DISTINCT MatchId FROM dbo.FbOdds WHERE OddsType='EU' AND CompanyId=0
) o ON o.MatchId = c.MatchId
LEFT JOIN (
  SELECT DISTINCT MatchId FROM dbo.FbExchange
) e ON e.MatchId = c.MatchId
WHERE c.Board='jc'
  AND c.CapturedAt = (SELECT MAX(CapturedAt) FROM dbo.BettingCard WHERE Board='jc')
ORDER BY hasEu, hasEx, c.MatchNo;
