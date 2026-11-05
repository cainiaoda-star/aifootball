/* ============================================================
   校准闭环：把「模型预测的命中率」和「当时的可下注水位」一起落库。

   之前只存 Confidence，而 Confidence 是经过下限（60）、经验降权（55）、
   平局压制（45）多次改写后的展示值，不是模型原始输出。
   拿它做校准统计，等于用被抹过的数验证自己，永远看不出 55% 门槛准不准。

   新增列都是「当时的事实」，赛后可只读，不再被任何规则改写。
   ============================================================ */

IF COL_LENGTH(N'dbo.Recommendation', N'HitProbability') IS NULL
    ALTER TABLE dbo.Recommendation ADD HitProbability DECIMAL(6,2) NULL;
GO

IF COL_LENGTH(N'dbo.Recommendation', N'HitProbNote') IS NULL
    ALTER TABLE dbo.Recommendation ADD HitProbNote NVARCHAR(200) NULL;
GO

/* 推荐方向当时的赔率（竞彩 SP / 欧赔终盘）。没有它就无法计算期望回报。 */
IF COL_LENGTH(N'dbo.Recommendation', N'PickOdds') IS NULL
    ALTER TABLE dbo.Recommendation ADD PickOdds DECIMAL(8,3) NULL;
GO

/* 市场去水后的隐含概率（0-100）。这是模型的基准线：预测值打不过它，模型就没有存在价值。 */
IF COL_LENGTH(N'dbo.Recommendation', N'MarketProb') IS NULL
    ALTER TABLE dbo.Recommendation ADD MarketProb DECIMAL(6,2) NULL;
GO

/* 产生这条预测的模型标识：devig=只去水 / logistic=五特征回归；附带样本数，便于回溯 */
IF COL_LENGTH(N'dbo.Recommendation', N'ModelTag') IS NULL
    ALTER TABLE dbo.Recommendation ADD ModelTag VARCHAR(60) NULL;
GO

/* 生成推荐时，该场最新盘口数据的采集时间。用来事后判断「当时用的是不是新鲜盘」。 */
IF COL_LENGTH(N'dbo.Recommendation', N'DataAt') IS NULL
    ALTER TABLE dbo.Recommendation ADD DataAt DATETIME2(0) NULL;
GO

/* 校准统计按预测值和日期分组，加个索引。 */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Recommendation_Calibration'
               AND object_id = OBJECT_ID(N'dbo.Recommendation'))
    CREATE INDEX IX_Recommendation_Calibration
        ON dbo.Recommendation (RecDate, HitProbability)
        INCLUDE (Board, RecResult, ActualResult, Hit, PickOdds, SignalLevel)
        WHERE HitProbability IS NOT NULL;
GO
