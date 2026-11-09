/* 北单只卖让球胜平负：推荐记录存让球数，按让球结算。 */
IF COL_LENGTH(N'dbo.Recommendation', N'Handicap') IS NULL
    ALTER TABLE dbo.Recommendation ADD Handicap DECIMAL(4,2) NULL;
GO
IF COL_LENGTH(N'dbo.Recommendation', N'HandicapHit') IS NULL
    ALTER TABLE dbo.Recommendation ADD HandicapHit BIT NULL;
GO
