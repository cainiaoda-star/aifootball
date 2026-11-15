/* =============================================================
   24_drop_dup_jc_index.sql
   删除 IX_JcOddsHistory_MatchId（2026-11-10）

   这张表 600 万行、挂着 6 棵 B 树（1 聚簇 + 5 非聚簇），
   而 IX_JcOddsHistory_MatchId 与 IX_JcOddsHistory_MatchPlay 的键列完全相同：
       MatchId, PlayType, CapturedAt
   差别只在 MatchId 那个多了 INCLUDE (OptionCode, OptionName, Odds)。

   实测使用次数（自上次 SQL Server 重启起，sys.dm_db_index_usage_stats）：
       索引                         seek   scan   update
       IX_JcOddsHistory_MatchPlay  25274   153   241000
       IX_JcOddsHistory_MatchId       15     0   241000
       IX_JcOddsHistory_Move        3552     0   241000
       IX_JcOddsHistory_NoPlayOpt   1011     4   241000
       IX_JcOddsHistory_Query        916     5   241000

   MatchId 那个基本没被选中过（15 次，可能是初始化时的偶发）：
   优化器更偏好体积更小的 MatchPlay（349MB vs 543MB）算出的成本更低。
   于是我们为它付了两份代价 —— 每写一行多维护一棵 543MB 的 B 树，
   外加 543MB 空间 —— 换来 15 次 seek。

   删的是 MatchId 这个（留 MatchPlay），因为它使用率低两个数量级。
   若将来出现「只按 MatchId 取、要一次性带出 OptionCode/Odds」的查询，
   MatchPlay 会回表，届时再按实际查询补 INCLUDE 或重建，不要凭感觉留两棵。

   背景：删它的直接动因是 jc-odds-backfill 反复「执行超时已过期」。
   分段计时定位到 trend/diag 两条重算 SQL 占 90% 时间（见 JcOddsRepository
   的 TrendBatchMatches 注释），插入本不是瓶颈；但写入侧的索引维护
   是每一次采样的固定成本，冗余的那一棵没有理由继续留着。

   回滚：CREATE NONCLUSTERED INDEX IX_JcOddsHistory_MatchId
             ON dbo.FbJcOddsHistory (MatchId, PlayType, CapturedAt DESC)
             INCLUDE (OptionCode, OptionName, Odds)
             WITH (FILLFACTOR = 90);
   ============================================================= */
SET NOCOUNT ON;
GO

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.FbJcOddsHistory')
      AND name = N'IX_JcOddsHistory_MatchId'
)
BEGIN
    DROP INDEX IX_JcOddsHistory_MatchId ON dbo.FbJcOddsHistory;
    PRINT 'IX_JcOddsHistory_MatchId 已删除（与 MatchPlay 键列重复，seek 15 次 vs 25274 次）';
END
ELSE
    PRINT 'IX_JcOddsHistory_MatchId 不存在，跳过';
GO
