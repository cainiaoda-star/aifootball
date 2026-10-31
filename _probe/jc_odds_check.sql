SELECT
  (SELECT COUNT(*) FROM dbo.BettingCard WHERE Board='jc' AND CapturedAt=(SELECT MAX(CapturedAt) FROM dbo.BettingCard WHERE Board='jc')) AS cards,
  (SELECT COUNT(DISTINCT c.MatchId) FROM dbo.BettingCard c
    INNER JOIN dbo.FbOdds o ON o.MatchId=c.MatchId AND o.OddsType='EU' AND o.CompanyId=0
   WHERE c.Board='jc' AND c.CapturedAt=(SELECT MAX(CapturedAt) FROM dbo.BettingCard WHERE Board='jc') AND c.MatchId IS NOT NULL) AS with_eu,
  (SELECT COUNT(*) FROM (
     SELECT c.MatchId FROM dbo.BettingCard c
     INNER JOIN dbo.FbOdds o ON o.MatchId=c.MatchId AND o.OddsType='EU' AND o.CompanyId=0
     WHERE c.Board='jc' AND c.CapturedAt=(SELECT MAX(CapturedAt) FROM dbo.BettingCard WHERE Board='jc') AND c.MatchId IS NOT NULL
     GROUP BY c.MatchId
     HAVING MAX(CASE WHEN o.IsOpening=1 THEN 1 ELSE 0 END)=1 AND MAX(CASE WHEN o.IsOpening=0 THEN 1 ELSE 0 END)=1
  ) x) AS with_open_and_close,
  (SELECT COUNT(DISTINCT c.MatchId) FROM dbo.BettingCard c
    INNER JOIN dbo.FbExchange e ON e.MatchId=c.MatchId
   WHERE c.Board='jc' AND c.CapturedAt=(SELECT MAX(CapturedAt) FROM dbo.BettingCard WHERE Board='jc') AND c.MatchId IS NOT NULL) AS with_ex;

SELECT TOP 8 c.MatchNo, c.MatchId,
  MAX(CASE WHEN o.IsOpening=1 THEN o.HomeWin END) AS openH,
  MAX(CASE WHEN o.IsOpening=0 THEN o.HomeWin END) AS closeH,
  MAX(CASE WHEN o.IsOpening=0 THEN o.HomeWin END)-MAX(CASE WHEN o.IsOpening=1 THEN o.HomeWin END) AS dH
FROM dbo.BettingCard c
LEFT JOIN dbo.FbOdds o ON o.MatchId=c.MatchId AND o.OddsType='EU' AND o.CompanyId=0
WHERE c.Board='jc' AND c.CapturedAt=(SELECT MAX(CapturedAt) FROM dbo.BettingCard WHERE Board='jc')
GROUP BY c.MatchNo, c.MatchId
ORDER BY CASE WHEN MAX(CASE WHEN o.IsOpening=0 THEN o.HomeWin END)-MAX(CASE WHEN o.IsOpening=1 THEN o.HomeWin END) IS NULL THEN 1 ELSE 0 END, c.MatchNo;
