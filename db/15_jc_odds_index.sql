/* =============================================================
   15_jc_odds_index.sql
   竞彩赔率历史表补 MatchId 索引（2026-11-04）

   问题：FbJcOddsHistory 只有 IX_JcOddsHistory_Query (MatchNo, PlayType, CapturedAt)。
   而大奶引擎取最新赔率走的是 `WHERE MatchId IN @ids`（JcOddsRepository.GetLatestAsync /
   GetLatestPlayAsync），没有 MatchId 索引 → 每次推荐重算都对 110 万行做全表扫。
   赔率回补按「有无竞彩场次」排序时也同样扫表，实测直接超时。

   索引列顺序对齐查询形态：
     MatchId   等值过滤
     PlayType  分组
     CapturedAt 取最新（DESC 让 ROW_NUMBER 直接走索引序）
   覆盖列带出 OptionCode/OptionName/Odds，避免回表。
   ============================================================= */
SET NOCOUNT ON;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.FbJcOddsHistory')
      AND name = N'IX_JcOddsHistory_MatchId'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_JcOddsHistory_MatchId
        ON dbo.FbJcOddsHistory (MatchId, PlayType, CapturedAt DESC)
        INCLUDE (OptionCode, OptionName, Odds)
        WITH (ONLINE = OFF, FILLFACTOR = 90);
    PRINT 'IX_JcOddsHistory_MatchId 已创建';
END
ELSE
    PRINT 'IX_JcOddsHistory_MatchId 已存在，跳过';
GO
