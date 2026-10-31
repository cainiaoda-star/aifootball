-- Q0: sample sizes
SELECT 'FbMatch_finished' AS k, COUNT(*) AS n FROM dbo.FbMatch WHERE Status=3 AND ResultSpf IS NOT NULL
UNION ALL SELECT 'FbOdds_rows', COUNT(*) FROM dbo.FbOdds
UNION ALL SELECT 'FbOdds_EU_open_avg', COUNT(*) FROM dbo.FbOdds WHERE OddsType='EU' AND IsOpening=1 AND CompanyId=0
UNION ALL SELECT 'FbOdds_EU_close_avg', COUNT(*) FROM dbo.FbOdds WHERE OddsType='EU' AND IsOpening=0 AND CompanyId=0
UNION ALL SELECT 'FbExchange_matches', COUNT(DISTINCT MatchId) FROM dbo.FbExchange
UNION ALL SELECT 'Recommendation_total', COUNT(*) FROM dbo.Recommendation
UNION ALL SELECT 'Recommendation_settled', COUNT(*) FROM dbo.Recommendation WHERE Hit IS NOT NULL;
