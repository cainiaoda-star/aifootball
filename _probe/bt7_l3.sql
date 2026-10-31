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
  SELECT f.ResultSpf, od.d, e.RatioHome, e.RatioAway FROM fin f
  JOIN od ON f.MatchId=od.MatchId JOIN ex e ON f.MatchId=e.MatchId AND e.rn=1
)
SELECT 'bigRise+awayHot60 ->0' AS tag, COUNT(*) n,
  ROUND(100.0*SUM(CASE WHEN ResultSpf='0' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0),1) targetPct
FROM x WHERE d>0.20 AND RatioAway>=60
UNION ALL SELECT 'awayHot75 ->0', COUNT(*),
  ROUND(100.0*SUM(CASE WHEN ResultSpf='0' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0),1)
FROM x WHERE RatioAway>=75
UNION ALL SELECT 'smallDrop+homeHot60-90 ->3', COUNT(*),
  ROUND(100.0*SUM(CASE WHEN ResultSpf='3' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0),1)
FROM x WHERE d<-0.05 AND d>-0.20 AND RatioHome>=60 AND RatioHome<90
UNION ALL SELECT 'anyRise+awayHot60 ->0', COUNT(*),
  ROUND(100.0*SUM(CASE WHEN ResultSpf='0' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0),1)
FROM x WHERE d>0.05 AND RatioAway>=60
UNION ALL SELECT 'oddsUp+homeHot TRUST HOME ->3', COUNT(*),
  ROUND(100.0*SUM(CASE WHEN ResultSpf='3' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0),1)
FROM x WHERE d>0.05 AND RatioHome>=60
UNION ALL SELECT 'oddsDown+awayHot TRUST AWAY ->0', COUNT(*),
  ROUND(100.0*SUM(CASE WHEN ResultSpf='0' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0),1)
FROM x WHERE d<-0.05 AND RatioAway>=60
UNION ALL SELECT 'bigDrop alone ->0 fade', COUNT(*),
  ROUND(100.0*SUM(CASE WHEN ResultSpf='0' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0),1)
FROM x WHERE d<=-0.20
UNION ALL SELECT 'bigRise alone ->0', COUNT(*),
  ROUND(100.0*SUM(CASE WHEN ResultSpf='0' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0),1)
FROM x WHERE d>0.20
UNION ALL SELECT 'baseline home rate in set', COUNT(*),
  ROUND(100.0*SUM(CASE WHEN ResultSpf='3' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0),1)
FROM x
UNION ALL SELECT 'baseline away rate in set', COUNT(*),
  ROUND(100.0*SUM(CASE WHEN ResultSpf='0' THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0),1)
FROM x
