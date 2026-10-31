/*
  推荐记录被同一「板块+日期+场次号」的新一期覆盖时，旧版保存保留了上一场的赛果。
  这里按完场比分重写对不上的行。可以重复跑。
*/
SET XACT_ABORT ON;
BEGIN TRAN;

UPDATE r
SET ActualScore  = CONCAT(f.HomeScore, '-', f.AwayScore),
    ActualGoals  = f.HomeScore + f.AwayScore,
    ActualResult = f.ResultSpf,
    Hit = CASE
        WHEN r.IsCoverAll = 1 OR r.RecResult IS NULL THEN NULL
        WHEN r.RecResult = f.ResultSpf THEN 1 ELSE 0 END
FROM dbo.Recommendation r
JOIN dbo.FbMatch f ON f.MatchId = r.MatchId
WHERE f.Status = 3
  AND f.HomeScore IS NOT NULL
  AND r.ActualResult IS NOT NULL
  AND (ISNULL(r.ActualScore, '') <> CONCAT(f.HomeScore, '-', f.AwayScore)
       OR r.ActualResult <> f.ResultSpf);

COMMIT;
