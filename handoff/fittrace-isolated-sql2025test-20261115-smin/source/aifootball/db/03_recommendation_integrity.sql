/* 推荐记录：全包不再占用平局，同一板块同一天同一场次只保留一条。可重复执行。 */
USE AiFootBall;
GO

IF COL_LENGTH(N'dbo.Recommendation', N'SignalLevel') IS NULL
    ALTER TABLE dbo.Recommendation ADD SignalLevel INT NULL;
GO

IF COL_LENGTH(N'dbo.Recommendation', N'IsCoverAll') IS NULL
    ALTER TABLE dbo.Recommendation ADD IsCoverAll BIT NOT NULL
        CONSTRAINT DF_Recommendation_IsCoverAll DEFAULT 0;
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Recommendation')
      AND name = N'RecResult'
      AND is_nullable = 0)
    ALTER TABLE dbo.Recommendation ALTER COLUMN RecResult CHAR(1) NULL;
GO

/* 旧数据里「全包」用 RecResult='1' 且置信度为空占位，平局会被算成命中。 */
UPDATE dbo.Recommendation
SET IsCoverAll = 1,
    RecResult = NULL,
    Hit = NULL
WHERE ISNULL(IsCoverAll, 0) = 0
  AND Confidence IS NULL
  AND RecResult = '1';
GO

WITH cte AS (
    SELECT RecId,
           ROW_NUMBER() OVER (
               PARTITION BY Board, RecDate, MatchNo
               ORDER BY RecId DESC) AS rn
    FROM dbo.Recommendation
    WHERE MatchNo IS NOT NULL
)
DELETE r
FROM dbo.Recommendation r
INNER JOIN cte ON cte.RecId = r.RecId
WHERE cte.rn > 1;
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UQ_Recommendation_Slot'
      AND object_id = OBJECT_ID(N'dbo.Recommendation'))
    CREATE UNIQUE INDEX UQ_Recommendation_Slot
        ON dbo.Recommendation (Board, RecDate, MatchNo)
        WHERE MatchNo IS NOT NULL;
GO

IF COL_LENGTH(N'dbo.Recommendation', N'RecHandicap') IS NULL
    ALTER TABLE dbo.Recommendation ADD RecHandicap CHAR(1) NULL;
GO

IF COL_LENGTH(N'dbo.Recommendation', N'RecHalfFull') IS NULL
    ALTER TABLE dbo.Recommendation ADD RecHalfFull NVARCHAR(20) NULL;
GO
