SELECT 'by_board' k, Board, CAST(NULL AS varchar(20)) confBand, COUNT(*) n,
  SUM(CASE WHEN Hit=1 THEN 1 ELSE 0 END) hits,
  ROUND(100.0*SUM(CASE WHEN Hit=1 THEN 1 ELSE 0 END)/NULLIF(SUM(CASE WHEN Hit IS NOT NULL THEN 1 ELSE 0 END),0),1) hitPct,
  SUM(CASE WHEN Hit IS NOT NULL THEN 1 ELSE 0 END) settled
FROM dbo.Recommendation
GROUP BY Board
UNION ALL
SELECT 'by_conf', 'ALL',
  CASE
    WHEN Confidence IS NULL THEN 'null'
    WHEN Confidence >= 80 THEN '80+'
    WHEN Confidence >= 60 THEN '60-80'
    WHEN Confidence >= 45 THEN '45-60'
    ELSE '<45'
  END,
  COUNT(*), SUM(CASE WHEN Hit=1 THEN 1 ELSE 0 END),
  ROUND(100.0*SUM(CASE WHEN Hit=1 THEN 1 ELSE 0 END)/NULLIF(SUM(CASE WHEN Hit IS NOT NULL THEN 1 ELSE 0 END),0),1),
  SUM(CASE WHEN Hit IS NOT NULL THEN 1 ELSE 0 END)
FROM dbo.Recommendation
GROUP BY CASE
    WHEN Confidence IS NULL THEN 'null'
    WHEN Confidence >= 80 THEN '80+'
    WHEN Confidence >= 60 THEN '60-80'
    WHEN Confidence >= 45 THEN '45-60'
    ELSE '<45'
  END
ORDER BY k, Board, confBand
