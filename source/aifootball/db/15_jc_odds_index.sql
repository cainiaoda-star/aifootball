/* =============================================================
   15_jc_odds_index.sql
   竞彩赔率历史表补 MatchId 索引（2026-11-04）

   ★ 2026-11-10 废弃：本脚本原来创建 IX_JcOddsHistory_MatchId，
     后来发现它与 IX_JcOddsHistory_MatchPlay 的键列完全相同
     (MatchId, PlayType, CapturedAt)，实测 seek 次数 15 vs 25274，
     白占 543MB 并让每次写入多维护一棵 B 树。已由 24_drop_dup_jc_index.sql 删除。
     现在这里只保留幂等保护：已有等价索引就什么都不做。

   原始问题（留档）：FbJcOddsHistory 只有 IX_JcOddsHistory_Query
   (MatchNo, PlayType, CapturedAt)，而大奶引擎取最新赔率走
   `WHERE MatchId IN @ids`（JcOddsRepository.GetLatestAsync / GetLatestPlayAsync），
   没有 MatchId 索引 → 每次推荐重算都对 110 万行做全表扫。
   ============================================================= */
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.FbJcOddsHistory', N'U') IS NULL
BEGIN
    PRINT 'FbJcOddsHistory 不存在，跳过';
    RETURN;
END
GO

-- 任何以 MatchId 打头的非聚簇索引都能满足「按场次取价」，不必重建第二棵。
IF EXISTS (
    SELECT 1
    FROM sys.indexes i
    JOIN sys.index_columns ic
      ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
    JOIN sys.columns c
      ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.object_id = OBJECT_ID(N'dbo.FbJcOddsHistory')
      AND i.is_primary_key = 0
      AND i.type = 2
      AND c.name = N'MatchId'
)
    PRINT '已有以 MatchId 打头的索引，跳过（2026-11-10 起不再建重复索引）';
ELSE
BEGIN
    -- 只有真的什么都缺时才建，且不再叫 MatchId 这个名字以避免历史包袱。
    CREATE NONCLUSTERED INDEX IX_JcOddsHistory_ByMatch
        ON dbo.FbJcOddsHistory (MatchId, PlayType, CapturedAt DESC)
        INCLUDE (OptionCode, OptionName, Odds)
        WITH (ONLINE = OFF, FILLFACTOR = 90);
    PRINT 'IX_JcOddsHistory_ByMatch 已创建（本表原本没有任何 MatchId 索引）';
END
GO
