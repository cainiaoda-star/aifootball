-- 21 俱乐部画像（2026-11-07）
-- 每队一行：全历史（主客合并）的反转率 / 零封率 / 半场平转分胜负率。
-- 口径 = FbDailySkill SkillId=4（教训1h，39.5万场实测）：
--   反转率     = 半全场 6 个反转档（半场方向≠全场方向）合计占比；
--   零封率     = 该队零封对手（对手全场 0 球）占比；
--   半场平转率 = 半场平且全场分胜负占比。
-- 重刷：整表 DELETE 后重灌（全历史重算 <10s，不需要增量）。
-- 供 MilkPickEngine 消费：热门方反转率≥38% → 半全场反向档提级；
-- 碾压型真强队（反转低+零封低，拜仁/巴黎/勒沃库森档）不做反向。

IF OBJECT_ID('dbo.FbTeamProfile') IS NULL
BEGIN
    CREATE TABLE dbo.FbTeamProfile
    (
        TeamName        nvarchar(100) NOT NULL CONSTRAINT PK_FbTeamProfile PRIMARY KEY,
        Games           int           NOT NULL,
        ReversalRate    decimal(5,1)  NOT NULL,
        CleanSheetRate  decimal(5,1)  NOT NULL,
        HalfDrawTurnRate decimal(5,1) NOT NULL,
        UpdatedAt       datetime2     NOT NULL CONSTRAINT DF_FbTeamProfile_UpdatedAt DEFAULT SYSDATETIME()
    );
END;
GO

DELETE FROM dbo.FbTeamProfile;

INSERT INTO dbo.FbTeamProfile (TeamName, Games, ReversalRate, CleanSheetRate, HalfDrawTurnRate)
SELECT Team,
       COUNT(*)                                                            AS Games,
       CAST(SUM(CASE WHEN HalfDir <> FullDir THEN 1 ELSE 0 END) * 100.0
            / COUNT(*)                                                     AS decimal(5,1)) AS ReversalRate,
       CAST(SUM(CASE WHEN Ags = 0 THEN 1 ELSE 0 END) * 100.0
            / COUNT(*)                                                     AS decimal(5,1)) AS CleanSheetRate,
       CAST(SUM(CASE WHEN HalfDir = '1' AND FullDir <> '1' THEN 1 ELSE 0 END) * 100.0
            / COUNT(*)                                                     AS decimal(5,1)) AS HalfDrawTurnRate
FROM (
    SELECT m.HomeTeamName AS Team,
           CASE WHEN m.HalfHomeScore > m.HalfAwayScore THEN '3'
                WHEN m.HalfHomeScore = m.HalfAwayScore THEN '1'
                ELSE '0' END AS HalfDir,
           CASE WHEN m.HomeScore > m.AwayScore THEN '3'
                WHEN m.HomeScore = m.AwayScore THEN '1'
                ELSE '0' END AS FullDir,
           m.AwayScore AS Ags
    FROM dbo.FbMatch m
    WHERE m.Status = 3 AND m.HalfHomeScore IS NOT NULL AND m.HomeTeamName <> ''
    UNION ALL
    SELECT m.AwayTeamName,
           CASE WHEN m.HalfHomeScore > m.HalfAwayScore THEN '3'
                WHEN m.HalfHomeScore = m.HalfAwayScore THEN '1'
                ELSE '0' END,
           CASE WHEN m.HomeScore > m.AwayScore THEN '3'
                WHEN m.HomeScore = m.AwayScore THEN '1'
                ELSE '0' END,
           m.HomeScore
    FROM dbo.FbMatch m
    WHERE m.Status = 3 AND m.HalfHomeScore IS NOT NULL AND m.AwayTeamName <> ''
) x
GROUP BY Team
HAVING COUNT(*) >= 30;
GO
