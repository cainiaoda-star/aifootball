-- SELECT-only | upcoming not-started | real FbMatch columns (LeagueName/HomeTeamName/AwayTeamName; no NameCN)
-- Params: none (SYSDATETIME gate). ApplicationIntent=ReadOnly is NOT sufficient alone.
SELECT m.MatchId,
       ISNULL(m.LeagueName, '') AS League,
       ISNULL(m.HomeTeamName, '') AS Home,
       ISNULL(m.AwayTeamName, '') AS Away,
       m.MatchTime AS Kickoff,
       CAST(NULL AS decimal(8,2)) AS OuLine,
       m.HomeScore,
       m.AwayScore,
       m.Status,
       CAST(NULL AS datetime2) AS SettledAt
FROM dbo.FbMatch m WITH (READUNCOMMITTED)
WHERE m.MatchTime > SYSDATETIME()
  AND m.HomeScore IS NULL
  AND m.AwayScore IS NULL
ORDER BY m.MatchTime, m.MatchId;
