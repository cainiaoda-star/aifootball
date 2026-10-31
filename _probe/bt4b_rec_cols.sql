SELECT TOP 5 COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Recommendation' ORDER BY ORDINAL_POSITION;
SELECT TOP 15 RecId, Board, MatchNo, RecResult, Confidence, Hit, ActualResult, LEFT(SignalDesc,80) sd
FROM dbo.Recommendation WHERE Hit IS NOT NULL ORDER BY RecId DESC;
SELECT
  CASE
    WHEN SignalDesc LIKE N'%双信号%' OR SignalDesc LIKE N'%大热%+%' OR Confidence>=80 THEN 'L3ish'
    WHEN Confidence>=55 AND Confidence<80 THEN 'L2ish'
    WHEN Confidence IS NOT NULL AND Confidence<55 THEN 'L1ish'
    ELSE 'other'
  END band,
  COUNT(*) n,
  SUM(CASE WHEN Hit=1 THEN 1 ELSE 0 END) hits,
  ROUND(100.0*SUM(CASE WHEN Hit=1 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0),1) hitPct
FROM dbo.Recommendation WHERE Hit IS NOT NULL
GROUP BY CASE
    WHEN SignalDesc LIKE N'%双信号%' OR SignalDesc LIKE N'%大热%+%' OR Confidence>=80 THEN 'L3ish'
    WHEN Confidence>=55 AND Confidence<80 THEN 'L2ish'
    WHEN Confidence IS NOT NULL AND Confidence<55 THEN 'L1ish'
    ELSE 'other'
  END
