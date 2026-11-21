-- SELECT-only | declared match results by parameterized OR batch (not list-parameter binding)
-- Script expands: WHERE (MatchId=@p0 OR MatchId=@p1 OR ...)
-- Finished proof: Status=3 AND both scores NOT NULL. Incomplete scores are NOT final.
SELECT m.MatchId,
       ISNULL(m.LeagueName, '') AS League,
       ISNULL(m.HomeTeamName, '') AS Home,
       ISNULL(m.AwayTeamName, '') AS Away,
       m.MatchTime AS Kickoff,
       CAST(NULL AS decimal(8,2)) AS OuLine,
       m.HomeScore,
       m.AwayScore,
       m.Status,
       CASE
         WHEN m.Status = 3 AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
           THEN COALESCE(m.UpdatedAt, DATEADD(minute, 150, m.MatchTime))
         ELSE NULL
       END AS SettledAt
FROM dbo.FbMatch m WITH (READUNCOMMITTED)
WHERE (/* @p0 OR @p1 ... injected by cycle script */);
