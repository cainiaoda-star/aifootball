WITH fin AS (
  SELECT MatchId, ResultSpf FROM dbo.FbMatch WHERE Status=3 AND ResultSpf IS NOT NULL
),
od AS (
  SELECT m.MatchId, c.HomeWin - o.HomeWin AS d
  FROM fin m
  JOIN dbo.FbOdds o ON o.MatchId=m.MatchId AND o.OddsType='EU' AND o.IsOpening=1 AND o.CompanyId=0
  JOIN dbo.FbOdds c ON c.MatchId=m.MatchId AND c.OddsType='EU' AND c.IsOpening=0 AND c.CompanyId=0
  WHERE o.HomeWin IS NOT NULL AND c.HomeWin IS NOT NULL
),
ex AS (
  SELECT e.MatchId, e.RatioHome, e.RatioAway,
         ROW_NUMBER() OVER (PARTITION BY e.MatchId ORDER BY e.CapturedAt DESC) rn
  FROM dbo.FbExchange e WHERE e.MatchId IN (SELECT MatchId FROM fin)
),
x AS (
  SELECT f.ResultSpf, od.d, e.RatioHome, e.RatioAway
  FROM fin f JOIN od ON f.MatchId=od.MatchId JOIN ex e ON f.MatchId=e.MatchId AND e.rn=1
),
scored AS (
  SELECT *,
    -- CURRENT engine pick (simplified, no fallback)
    CASE
      WHEN d < -0.05 AND RatioHome >= 60 THEN '3'
      WHEN d >  0.05 AND RatioAway >= 60 THEN '0'
      WHEN d < -0.05 AND RatioAway >= 60 THEN '3'
      WHEN d >  0.05 AND RatioHome >= 60 THEN '0'
      WHEN d < -0.05 THEN '3'
      WHEN d >  0.05 THEN '0'
      WHEN RatioHome >= 60 THEN '3'
      WHEN RatioAway >= 60 THEN '0'
      ELSE NULL
    END AS curPick,
    CASE
      WHEN d < -0.05 AND RatioHome >= 60 THEN 3
      WHEN d >  0.05 AND RatioAway >= 60 THEN 3
      WHEN d < -0.05 AND RatioAway >= 60 THEN 2
      WHEN d >  0.05 AND RatioHome >= 60 THEN 2
      WHEN d < -0.05 THEN 2
      WHEN d >  0.05 THEN 2
      WHEN RatioHome >= 60 THEN 2
      WHEN RatioAway >= 60 THEN 2
      ELSE 0
    END AS curLevel,
    -- PROPOSED:
    -- L3: bigRise+awayHot60 OR awayHot75 OR (smallDrop + homeHot60 + NOT homeHot extreme without odds)
    -- L3 home: smallDrop (-0.20, -0.05) + homeHot60
    -- L3 away: bigRise (>0.20) + awayHot60; OR awayHot75 alone
    -- NEVER recommend on homeHot alone
    -- Conflict: trust HOT not odds (reverse of current)
    -- bigDrop alone -> away (fade) or skip
    -- oddsDown alone without hot -> skip or away
    CASE
      WHEN d > 0.20 AND RatioAway >= 60 THEN '0'          -- L3 away strong
      WHEN RatioAway >= 75 THEN '0'                        -- L3 away hot75
      WHEN d < -0.05 AND d > -0.20 AND RatioHome >= 60 AND RatioHome < 90 THEN '3'  -- L3 home smallDrop+hot
      WHEN d > 0.05 AND RatioAway >= 60 THEN '0'           -- L2/3 oddsUp+awayHot
      WHEN d < -0.05 AND RatioAway >= 60 THEN '0'          -- conflict: trust away hot
      WHEN d > 0.05 AND RatioHome >= 60 THEN '3'           -- conflict: trust home hot
      WHEN d > 0.20 THEN '0'                               -- L2 bigRise alone -> away
      WHEN d < -0.05 AND d > -0.20 THEN NULL               -- smallDrop alone: SKIP (data shows bad)
      WHEN d <= -0.20 THEN '0'                             -- bigDrop alone: fade to away
      WHEN RatioAway >= 60 AND RatioAway < 75 THEN NULL    -- awayHot 60-75 alone weak (many draws): SKIP
      WHEN RatioHome >= 60 THEN NULL                       -- homeHot alone: SKIP (33% live hit)
      ELSE NULL
    END AS propPick,
    CASE
      WHEN d > 0.20 AND RatioAway >= 60 THEN 3
      WHEN RatioAway >= 75 THEN 3
      WHEN d < -0.05 AND d > -0.20 AND RatioHome >= 60 AND RatioHome < 90 THEN 3
      WHEN d > 0.05 AND RatioAway >= 60 THEN 2
      WHEN d < -0.05 AND RatioAway >= 60 THEN 2
      WHEN d > 0.05 AND RatioHome >= 60 THEN 2
      WHEN d > 0.20 THEN 2
      WHEN d <= -0.20 THEN 2
      ELSE 0
    END AS propLevel
  FROM x
)
SELECT 'CURRENT_all_picks' strategy, COUNT(*) n,
  SUM(CASE WHEN curPick IS NOT NULL THEN 1 ELSE 0 END) picks,
  SUM(CASE WHEN curPick = ResultSpf THEN 1 ELSE 0 END) hits,
  ROUND(100.0*SUM(CASE WHEN curPick = ResultSpf THEN 1 ELSE 0 END)/NULLIF(SUM(CASE WHEN curPick IS NOT NULL THEN 1 ELSE 0 END),0),1) hitPct
FROM scored
UNION ALL
SELECT 'CURRENT_L3_only', COUNT(*),
  SUM(CASE WHEN curLevel=3 THEN 1 ELSE 0 END),
  SUM(CASE WHEN curLevel=3 AND curPick=ResultSpf THEN 1 ELSE 0 END),
  ROUND(100.0*SUM(CASE WHEN curLevel=3 AND curPick=ResultSpf THEN 1 ELSE 0 END)/NULLIF(SUM(CASE WHEN curLevel=3 THEN 1 ELSE 0 END),0),1)
FROM scored
UNION ALL
SELECT 'CURRENT_L2_only', COUNT(*),
  SUM(CASE WHEN curLevel=2 THEN 1 ELSE 0 END),
  SUM(CASE WHEN curLevel=2 AND curPick=ResultSpf THEN 1 ELSE 0 END),
  ROUND(100.0*SUM(CASE WHEN curLevel=2 AND curPick=ResultSpf THEN 1 ELSE 0 END)/NULLIF(SUM(CASE WHEN curLevel=2 THEN 1 ELSE 0 END),0),1)
FROM scored
UNION ALL
SELECT 'PROPOSED_all_picks', COUNT(*),
  SUM(CASE WHEN propPick IS NOT NULL THEN 1 ELSE 0 END),
  SUM(CASE WHEN propPick = ResultSpf THEN 1 ELSE 0 END),
  ROUND(100.0*SUM(CASE WHEN propPick = ResultSpf THEN 1 ELSE 0 END)/NULLIF(SUM(CASE WHEN propPick IS NOT NULL THEN 1 ELSE 0 END),0),1)
FROM scored
UNION ALL
SELECT 'PROPOSED_L3_only', COUNT(*),
  SUM(CASE WHEN propLevel=3 THEN 1 ELSE 0 END),
  SUM(CASE WHEN propLevel=3 AND propPick=ResultSpf THEN 1 ELSE 0 END),
  ROUND(100.0*SUM(CASE WHEN propLevel=3 AND propPick=ResultSpf THEN 1 ELSE 0 END)/NULLIF(SUM(CASE WHEN propLevel=3 THEN 1 ELSE 0 END),0),1)
FROM scored
UNION ALL
SELECT 'PROPOSED_L2_only', COUNT(*),
  SUM(CASE WHEN propLevel=2 THEN 1 ELSE 0 END),
  SUM(CASE WHEN propLevel=2 AND propPick=ResultSpf THEN 1 ELSE 0 END),
  ROUND(100.0*SUM(CASE WHEN propLevel=2 AND propPick=ResultSpf THEN 1 ELSE 0 END)/NULLIF(SUM(CASE WHEN propLevel=2 THEN 1 ELSE 0 END),0),1)
FROM scored
