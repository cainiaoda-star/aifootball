-- 大奶历史粗探（A=全库完场）。细标签与 SP 中位数以 /api/milk/stats 为准。
-- 用法：在 SSMS 对 AiFootBall 库执行；或 sqlcmd。

DECLARE @from DATE = DATEADD(DAY, -90, CAST(GETDATE() AS DATE));
DECLARE @to   DATE = CAST(GETDATE() AS DATE);

;WITH finished AS (
  SELECT MatchId, MatchTime, LeagueName,
         HomeScore, AwayScore, HalfHomeScore, HalfAwayScore,
         HomeScore + AwayScore AS Goals
  FROM dbo.FbMatch
  WHERE Status = 3
    AND HomeScore IS NOT NULL AND AwayScore IS NOT NULL
    AND MatchTime >= @from AND MatchTime < DATEADD(DAY, 1, @to)
)
SELECT
  COUNT(*) AS MatchCount,
  SUM(CASE WHEN Goals >= 7 THEN 1 ELSE 0 END) AS Goals7p,
  SUM(CASE WHEN HalfHomeScore > HalfAwayScore AND HomeScore < AwayScore THEN 1 ELSE 0 END) AS HfWinLose,
  SUM(CASE WHEN HalfHomeScore < HalfAwayScore AND HomeScore > AwayScore THEN 1 ELSE 0 END) AS HfLoseWin,
  SUM(CASE WHEN HomeScore > AwayScore
            AND CONCAT(HomeScore, ':', AwayScore) NOT IN
              ('1:0','2:0','2:1','3:0','3:1','3:2','4:0','4:1','4:2','5:0','5:1','5:2')
           THEN 1 ELSE 0 END) AS ScoreOtherHome,
  SUM(CASE WHEN HomeScore < AwayScore
            AND CONCAT(HomeScore, ':', AwayScore) NOT IN
              ('0:1','0:2','1:2','0:3','1:3','2:3','0:4','1:4','2:4','0:5','1:5','2:5')
           THEN 1 ELSE 0 END) AS ScoreOtherAway
FROM finished;

-- 联赛 Top（任一粗标签）
;WITH finished AS (
  SELECT MatchId, ISNULL(LeagueName, N'未知') AS LeagueName,
         HomeScore, AwayScore, HalfHomeScore, HalfAwayScore,
         HomeScore + AwayScore AS Goals
  FROM dbo.FbMatch
  WHERE Status = 3
    AND HomeScore IS NOT NULL AND AwayScore IS NOT NULL
    AND MatchTime >= @from AND MatchTime < DATEADD(DAY, 1, @to)
),
tagged AS (
  SELECT LeagueName, MatchId
  FROM finished
  WHERE Goals >= 7
     OR (HalfHomeScore > HalfAwayScore AND HomeScore < AwayScore)
     OR (HalfHomeScore < HalfAwayScore AND HomeScore > AwayScore)
     OR (HomeScore > AwayScore AND CONCAT(HomeScore, ':', AwayScore) NOT IN
           ('1:0','2:0','2:1','3:0','3:1','3:2','4:0','4:1','4:2','5:0','5:1','5:2'))
     OR (HomeScore < AwayScore AND CONCAT(HomeScore, ':', AwayScore) NOT IN
           ('0:1','0:2','1:2','0:3','1:3','2:3','0:4','1:4','2:4','0:5','1:5','2:5'))
)
SELECT TOP 15 LeagueName, COUNT(*) AS MilkMatches
FROM tagged
GROUP BY LeagueName
ORDER BY MilkMatches DESC;
