IF COL_LENGTH('dbo.Recommendation','MatchTime') IS NULL ALTER TABLE dbo.Recommendation ADD MatchTime DATETIME2(0) NULL;
IF COL_LENGTH('dbo.Recommendation','Score1') IS NULL ALTER TABLE dbo.Recommendation ADD Score1 NVARCHAR(10) NULL;
IF COL_LENGTH('dbo.Recommendation','Score2') IS NULL ALTER TABLE dbo.Recommendation ADD Score2 NVARCHAR(10) NULL;
IF COL_LENGTH('dbo.Recommendation','Goals1') IS NULL ALTER TABLE dbo.Recommendation ADD Goals1 INT NULL;
IF COL_LENGTH('dbo.Recommendation','Goals2') IS NULL ALTER TABLE dbo.Recommendation ADD Goals2 INT NULL;
IF COL_LENGTH('dbo.Recommendation','ActualScore') IS NULL ALTER TABLE dbo.Recommendation ADD ActualScore NVARCHAR(10) NULL;
IF COL_LENGTH('dbo.Recommendation','ActualGoals') IS NULL ALTER TABLE dbo.Recommendation ADD ActualGoals INT NULL;
IF COL_LENGTH('dbo.Recommendation','RecDate') IS NULL ALTER TABLE dbo.Recommendation ADD RecDate DATE NULL CONSTRAINT DF_Recommendation_RecDate DEFAULT CAST(SYSDATETIME() AS DATE);
