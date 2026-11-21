-- SELECT-only | prekick JQ | time-window JOIN (FORBIDDEN: list-parameter MatchId binding)
-- CapturedAt < MatchTime; no current-price backfill.
SELECT h.MatchId,
       h.PlayType,
       h.OptionCode,
       h.Odds,
       h.CapturedAt
FROM dbo.FbJcOddsHistory h WITH (READUNCOMMITTED)
INNER JOIN dbo.FbMatch m WITH (READUNCOMMITTED) ON m.MatchId = h.MatchId
WHERE m.MatchTime > SYSDATETIME()
  AND m.HomeScore IS NULL
  AND m.AwayScore IS NULL
  AND h.PlayType = N'JQ'
  AND h.Odds > 1
  AND h.CapturedAt < m.MatchTime
ORDER BY h.MatchId, h.CapturedAt, h.OptionCode;
