SELECT
  CASE
    WHEN SignalDesc LIKE N'%下降%大热%' OR SignalDesc LIKE N'%下降)% + %大热%' THEN 'L3_down_homeHot'
    WHEN SignalDesc LIKE N'%上升%大热%' OR SignalDesc LIKE N'%上升)% + %大热%' THEN 'L3_up_awayHot'
    WHEN SignalDesc LIKE N'%信号冲突%主胜%' THEN 'conflict_to_home'
    WHEN SignalDesc LIKE N'%信号冲突%客胜%' THEN 'conflict_to_away'
    WHEN SignalDesc LIKE N'%赔率主胜下降%' THEN 'odds_down_only'
    WHEN SignalDesc LIKE N'%赔率主胜上升%' THEN 'odds_up_only'
    WHEN SignalDesc LIKE N'%必发主胜大热%' THEN 'homeHot_only'
    WHEN SignalDesc LIKE N'%必发客胜大热%' THEN 'awayHot_only'
    WHEN SignalDesc LIKE N'%赔率兜底%' THEN 'odds_fallback'
    WHEN SignalDesc LIKE N'%观望%' OR SignalDesc LIKE N'%全包%' THEN 'watch_or_pack'
    ELSE 'other'
  END AS sig,
  COUNT(*) n,
  SUM(CASE WHEN Hit=1 THEN 1 ELSE 0 END) hits,
  ROUND(100.0*SUM(CASE WHEN Hit=1 THEN 1 ELSE 0 END)/NULLIF(SUM(CASE WHEN Hit IS NOT NULL THEN 1 ELSE 0 END),0),1) hitPct,
  SUM(CASE WHEN Hit IS NOT NULL THEN 1 ELSE 0 END) settled,
  ROUND(AVG(Confidence),1) avgConf
FROM dbo.Recommendation
GROUP BY CASE
    WHEN SignalDesc LIKE N'%下降%大热%' OR SignalDesc LIKE N'%下降)% + %大热%' THEN 'L3_down_homeHot'
    WHEN SignalDesc LIKE N'%上升%大热%' OR SignalDesc LIKE N'%上升)% + %大热%' THEN 'L3_up_awayHot'
    WHEN SignalDesc LIKE N'%信号冲突%主胜%' THEN 'conflict_to_home'
    WHEN SignalDesc LIKE N'%信号冲突%客胜%' THEN 'conflict_to_away'
    WHEN SignalDesc LIKE N'%赔率主胜下降%' THEN 'odds_down_only'
    WHEN SignalDesc LIKE N'%赔率主胜上升%' THEN 'odds_up_only'
    WHEN SignalDesc LIKE N'%必发主胜大热%' THEN 'homeHot_only'
    WHEN SignalDesc LIKE N'%必发客胜大热%' THEN 'awayHot_only'
    WHEN SignalDesc LIKE N'%赔率兜底%' THEN 'odds_fallback'
    WHEN SignalDesc LIKE N'%观望%' OR SignalDesc LIKE N'%全包%' THEN 'watch_or_pack'
    ELSE 'other'
  END
ORDER BY settled DESC
