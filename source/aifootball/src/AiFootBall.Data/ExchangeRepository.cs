using System.Data;
using AiFootBall.Core.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AiFootBall.Data;

/// <summary>必发成交数据仓储：快照 + 大额明细 + 采集标记。</summary>
public sealed class ExchangeRepository
{
    private readonly SqlConnectionFactory _factory;

    public ExchangeRepository(SqlConnectionFactory factory) => _factory = factory;

    private const string StageSql = @"
IF OBJECT_ID('tempdb..#ExStage') IS NOT NULL DROP TABLE #ExStage;
CREATE TABLE #ExStage
(
    MatchId BIGINT NOT NULL, CapturedAt DATETIME2(0) NOT NULL, MinutesToKick INT NULL,
    EuroHome DECIMAL(8,3) NULL, EuroDraw DECIMAL(8,3) NULL, EuroAway DECIMAL(8,3) NULL,
    EuroProbHome DECIMAL(6,2) NULL, EuroProbDraw DECIMAL(6,2) NULL, EuroProbAway DECIMAL(6,2) NULL,
    PriceHome DECIMAL(8,3) NULL, PriceDraw DECIMAL(8,3) NULL, PriceAway DECIMAL(8,3) NULL,
    VolumeHome DECIMAL(18,2) NULL, VolumeDraw DECIMAL(18,2) NULL, VolumeAway DECIMAL(18,2) NULL,
    PnlHome DECIMAL(18,2) NULL, PnlDraw DECIMAL(18,2) NULL, PnlAway DECIMAL(18,2) NULL,
    RatioHome DECIMAL(6,2) NULL, RatioDraw DECIMAL(6,2) NULL, RatioAway DECIMAL(6,2) NULL,
    BdRatioHome DECIMAL(6,2) NULL, BdRatioDraw DECIMAL(6,2) NULL, BdRatioAway DECIMAL(6,2) NULL,
    BfIdxHome DECIMAL(6,2) NULL, BfIdxDraw DECIMAL(6,2) NULL, BfIdxAway DECIMAL(6,2) NULL,
    HotIdxHome DECIMAL(6,2) NULL, HotIdxDraw DECIMAL(6,2) NULL, HotIdxAway DECIMAL(6,2) NULL,
    PnlIdxHome DECIMAL(6,2) NULL, PnlIdxDraw DECIMAL(6,2) NULL, PnlIdxAway DECIMAL(6,2) NULL,
    TotalVolume DECIMAL(18,2) NULL, BigTotal DECIMAL(18,2) NULL,
    BigHome DECIMAL(18,2) NULL, BigDraw DECIMAL(18,2) NULL, BigAway DECIMAL(18,2) NULL,
    TipScale NVARCHAR(200) NULL, TipLean NVARCHAR(200) NULL
);
IF OBJECT_ID('tempdb..#ExTrade') IS NOT NULL DROP TABLE #ExTrade;
CREATE TABLE #ExTrade
(
    MatchId BIGINT NOT NULL, TradeTime DATETIME2(0) NOT NULL, Side CHAR(1) NOT NULL,
    Direction NVARCHAR(4) NOT NULL, Volume DECIMAL(18,4) NOT NULL,
    Ratio DECIMAL(6,2) NULL, CapturedAt DATETIME2(0) NOT NULL
);
IF OBJECT_ID('tempdb..#ExMark') IS NOT NULL DROP TABLE #ExMark;
CREATE TABLE #ExMark (MatchId BIGINT NOT NULL PRIMARY KEY, ExchangeStatus TINYINT NOT NULL,
                      ExchangeVolume DECIMAL(18,2) NULL);";

    private const string MergeSql = @"
MERGE dbo.FbExchange AS t
USING #ExStage AS s
   ON t.MatchId = s.MatchId AND t.CapturedAt = s.CapturedAt
WHEN MATCHED THEN UPDATE SET
    MinutesToKick = s.MinutesToKick,
    EuroHome = s.EuroHome, EuroDraw = s.EuroDraw, EuroAway = s.EuroAway,
    EuroProbHome = s.EuroProbHome, EuroProbDraw = s.EuroProbDraw, EuroProbAway = s.EuroProbAway,
    PriceHome = s.PriceHome, PriceDraw = s.PriceDraw, PriceAway = s.PriceAway,
    VolumeHome = s.VolumeHome, VolumeDraw = s.VolumeDraw, VolumeAway = s.VolumeAway,
    PnlHome = s.PnlHome, PnlDraw = s.PnlDraw, PnlAway = s.PnlAway,
    RatioHome = s.RatioHome, RatioDraw = s.RatioDraw, RatioAway = s.RatioAway,
    BdRatioHome = s.BdRatioHome, BdRatioDraw = s.BdRatioDraw, BdRatioAway = s.BdRatioAway,
    BfIdxHome = s.BfIdxHome, BfIdxDraw = s.BfIdxDraw, BfIdxAway = s.BfIdxAway,
    HotIdxHome = s.HotIdxHome, HotIdxDraw = s.HotIdxDraw, HotIdxAway = s.HotIdxAway,
    PnlIdxHome = s.PnlIdxHome, PnlIdxDraw = s.PnlIdxDraw, PnlIdxAway = s.PnlIdxAway,
    TotalVolume = s.TotalVolume, BigTotal = s.BigTotal,
    BigHome = s.BigHome, BigDraw = s.BigDraw, BigAway = s.BigAway,
    TipScale = s.TipScale, TipLean = s.TipLean
WHEN NOT MATCHED THEN INSERT
    (MatchId, CapturedAt, MinutesToKick,
     EuroHome, EuroDraw, EuroAway, EuroProbHome, EuroProbDraw, EuroProbAway,
     PriceHome, PriceDraw, PriceAway, VolumeHome, VolumeDraw, VolumeAway,
     PnlHome, PnlDraw, PnlAway, RatioHome, RatioDraw, RatioAway,
     BdRatioHome, BdRatioDraw, BdRatioAway,
     BfIdxHome, BfIdxDraw, BfIdxAway, HotIdxHome, HotIdxDraw, HotIdxAway,
     PnlIdxHome, PnlIdxDraw, PnlIdxAway, TotalVolume, BigTotal,
     BigHome, BigDraw, BigAway, TipScale, TipLean)
    VALUES (s.MatchId, s.CapturedAt, s.MinutesToKick,
            s.EuroHome, s.EuroDraw, s.EuroAway, s.EuroProbHome, s.EuroProbDraw, s.EuroProbAway,
            s.PriceHome, s.PriceDraw, s.PriceAway, s.VolumeHome, s.VolumeDraw, s.VolumeAway,
            s.PnlHome, s.PnlDraw, s.PnlAway, s.RatioHome, s.RatioDraw, s.RatioAway,
            s.BdRatioHome, s.BdRatioDraw, s.BdRatioAway,
            s.BfIdxHome, s.BfIdxDraw, s.BfIdxAway, s.HotIdxHome, s.HotIdxDraw, s.HotIdxAway,
            s.PnlIdxHome, s.PnlIdxDraw, s.PnlIdxAway, s.TotalVolume, s.BigTotal,
            s.BigHome, s.BigDraw, s.BigAway, s.TipScale, s.TipLean);

INSERT INTO dbo.FbExchangeTrade (MatchId, TradeTime, Side, Direction, Volume, Ratio, CapturedAt)
SELECT t.MatchId, t.TradeTime, t.Side, t.Direction, t.Volume, t.Ratio, t.CapturedAt
FROM #ExTrade t
WHERE NOT EXISTS (SELECT 1 FROM dbo.FbExchangeTrade x
                  WHERE x.MatchId = t.MatchId AND x.TradeTime = t.TradeTime
                    AND x.Side = t.Side AND x.Direction = t.Direction AND x.Volume = t.Volume);

UPDATE m SET m.ExchangeCheckedAt = SYSDATETIME(), m.ExchangeStatus = k.ExchangeStatus,
             m.ExchangeVolume = ISNULL(k.ExchangeVolume, m.ExchangeVolume)
FROM dbo.FbMatch m JOIN #ExMark k ON k.MatchId = m.MatchId;";

    public async Task<int> UpsertAsync(IReadOnlyList<MatchExchangeResult> results, CancellationToken ct = default)
    {
        if (results.Count == 0) return 0;

        var rows = results.Count(r => r.Status == 1);
        if (rows == 0)
        {
            // 只更新标记，避免建立空事务表
            await using var conn0 = await _factory.OpenAsync(ct);
            var marks = results.Select(r => new { r.MatchId, Status = (byte)r.Status });
            await conn0.ExecuteAsync(
                @"UPDATE m SET ExchangeCheckedAt = SYSDATETIME(), ExchangeStatus = @Status
                  FROM dbo.FbMatch m WHERE m.MatchId = @MatchId", marks);
            return 0;
        }

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(StageSql, transaction: tx);

            using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
                   { DestinationTableName = "#ExStage", BatchSize = 2000, BulkCopyTimeout = 120 })
            {
                foreach (var c in StageColumns) bulk.ColumnMappings.Add(c, c);
                await bulk.WriteToServerAsync(BuildStage(results), ct);
            }

            var trades = results.SelectMany(r => r.Trades).ToList();
            if (trades.Count > 0)
            {
                using var bulk2 = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
                    { DestinationTableName = "#ExTrade", BatchSize = 5000, BulkCopyTimeout = 120 };
                foreach (var c in new[] { "MatchId", "TradeTime", "Side", "Direction", "Volume", "Ratio", "CapturedAt" })
                    bulk2.ColumnMappings.Add(c, c);
                await bulk2.WriteToServerAsync(BuildTradeTable(trades), ct);
            }
            else
            {
                // 表已建但无数据，仍需保证后续语句可执行
                await conn.ExecuteAsync("DELETE FROM #ExTrade;", transaction: tx);
            }

            using (var bulk3 = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
                   { DestinationTableName = "#ExMark", BulkCopyTimeout = 120 })
            {
                bulk3.ColumnMappings.Add("MatchId", "MatchId");
                bulk3.ColumnMappings.Add("ExchangeStatus", "ExchangeStatus");
                bulk3.ColumnMappings.Add("ExchangeVolume", "ExchangeVolume");
                await bulk3.WriteToServerAsync(BuildMarkTable(results), ct);
            }

            await conn.ExecuteAsync(MergeSql, transaction: tx, commandTimeout: 180);
            await tx.CommitAsync(ct);
            return rows;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static readonly string[] StageColumns =
    {
        "MatchId", "CapturedAt", "MinutesToKick",
        "EuroHome", "EuroDraw", "EuroAway", "EuroProbHome", "EuroProbDraw", "EuroProbAway",
        "PriceHome", "PriceDraw", "PriceAway", "VolumeHome", "VolumeDraw", "VolumeAway",
        "PnlHome", "PnlDraw", "PnlAway", "RatioHome", "RatioDraw", "RatioAway",
        "BdRatioHome", "BdRatioDraw", "BdRatioAway",
        "BfIdxHome", "BfIdxDraw", "BfIdxAway", "HotIdxHome", "HotIdxDraw", "HotIdxAway",
        "PnlIdxHome", "PnlIdxDraw", "PnlIdxAway", "TotalVolume", "BigTotal",
        "BigHome", "BigDraw", "BigAway", "TipScale", "TipLean"
    };

    private static DataTable BuildStage(IEnumerable<MatchExchangeResult> results)
    {
        var dt = new DataTable();
        dt.Columns.Add("MatchId", typeof(long));
        dt.Columns.Add("CapturedAt", typeof(DateTime));
        dt.Columns.Add("MinutesToKick", typeof(int));
        foreach (var c in new[]
                 {
                     "EuroHome", "EuroDraw", "EuroAway", "EuroProbHome", "EuroProbDraw", "EuroProbAway",
                     "PriceHome", "PriceDraw", "PriceAway", "VolumeHome", "VolumeDraw", "VolumeAway",
                     "PnlHome", "PnlDraw", "PnlAway", "RatioHome", "RatioDraw", "RatioAway",
                     "BdRatioHome", "BdRatioDraw", "BdRatioAway",
                     "BfIdxHome", "BfIdxDraw", "BfIdxAway", "HotIdxHome", "HotIdxDraw", "HotIdxAway",
                     "PnlIdxHome", "PnlIdxDraw", "PnlIdxAway", "TotalVolume", "BigTotal",
                     "BigHome", "BigDraw", "BigAway"
                 })
            dt.Columns.Add(c, typeof(decimal));
        dt.Columns.Add("TipScale", typeof(string));
        dt.Columns.Add("TipLean", typeof(string));

        foreach (var r in results.Where(x => x.Status == 1 && x.Snapshot is not null))
        {
            var s = r.Snapshot!;
            dt.Rows.Add(
                s.MatchId, s.CapturedAt,
                (object?)s.MinutesToKick ?? DBNull.Value,
                D(s.EuroHome), D(s.EuroDraw), D(s.EuroAway),
                D(s.EuroProbHome), D(s.EuroProbDraw), D(s.EuroProbAway),
                D(s.PriceHome), D(s.PriceDraw), D(s.PriceAway),
                D(s.VolumeHome), D(s.VolumeDraw), D(s.VolumeAway),
                D(s.PnlHome), D(s.PnlDraw), D(s.PnlAway),
                D(s.RatioHome), D(s.RatioDraw), D(s.RatioAway),
                D(s.BdRatioHome), D(s.BdRatioDraw), D(s.BdRatioAway),
                D(s.BfIdxHome), D(s.BfIdxDraw), D(s.BfIdxAway),
                D(s.HotIdxHome), D(s.HotIdxDraw), D(s.HotIdxAway),
                D(s.PnlIdxHome), D(s.PnlIdxDraw), D(s.PnlIdxAway),
                D(s.TotalVolume), D(s.BigTotal),
                D(s.BigHome), D(s.BigDraw), D(s.BigAway),
                (object?)s.TipScale ?? DBNull.Value, (object?)s.TipLean ?? DBNull.Value);
        }
        return dt;
    }

    private static DataTable BuildTradeTable(IEnumerable<ExchangeTrade> trades)
    {
        var dt = new DataTable();
        dt.Columns.Add("MatchId", typeof(long));
        dt.Columns.Add("TradeTime", typeof(DateTime));
        dt.Columns.Add("Side", typeof(string));
        dt.Columns.Add("Direction", typeof(string));
        dt.Columns.Add("Volume", typeof(decimal));
        dt.Columns.Add("Ratio", typeof(decimal));
        dt.Columns.Add("CapturedAt", typeof(DateTime));
        foreach (var t in trades)
            dt.Rows.Add(t.MatchId, t.TradeTime, t.Side, t.Direction, t.Volume,
                (object?)t.Ratio ?? DBNull.Value, t.CapturedAt);
        return dt;
    }

    private static DataTable BuildMarkTable(IEnumerable<MatchExchangeResult> results)
    {
        var dt = new DataTable();
        dt.Columns.Add("MatchId", typeof(long));
        dt.Columns.Add("ExchangeStatus", typeof(byte));
        dt.Columns.Add("ExchangeVolume", typeof(decimal));
        foreach (var r in results)
            dt.Rows.Add(r.MatchId, (byte)r.Status,
                (object?)r.Snapshot?.TotalVolume ?? DBNull.Value);
        return dt;
    }

    private static object D(decimal? v) => (object?)v ?? DBNull.Value;

    /* ---------------- 查询 ---------------- */

    /// <summary>
    /// 取待采集必发的赛事。
    /// 在售 BettingCard 永远优先；有盘口按 SoldRecheck/Recheck 刷新；无盘口的在售场 45 分钟再试一次。
    /// </summary>
    public async Task<List<MatchBrief>> GetMatchesForExchangeAsync(DateTime from, DateTime to, int take,
        int recheckMinutes, CancellationToken ct = default)
    {
        // 在售场加密：不超过 12 分钟；其它场用作业配置的间隔
        var soldRecheck = Math.Clamp(recheckMinutes <= 0 ? 12 : Math.Min(recheckMinutes, 12), 8, 12);
        var normalRecheck = recheckMinutes <= 0 ? 25 : recheckMinutes;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<MatchBrief>(
            @"SELECT TOP (@Take) m.MatchId, m.MatchTime, m.HomeTeamName, m.AwayTeamName,
                     ISNULL(l.Name, N'') AS LeagueName
              FROM dbo.FbMatch m
              LEFT JOIN dbo.FbLeague l ON l.LeagueId = m.LeagueId
              OUTER APPLY (
                  SELECT TOP 1 1 AS OnSale
                  FROM dbo.BettingCard c
                  WHERE c.MatchId = m.MatchId
                    AND c.CapturedAt = (SELECT MAX(CapturedAt) FROM dbo.BettingCard b WHERE b.Board = c.Board)
              ) sale
              WHERE (
                      (m.MatchTime BETWEEN @From AND @To)
                      OR sale.OnSale IS NOT NULL
                    )
                AND m.MatchTime > DATEADD(HOUR, -2, SYSDATETIME())
                AND (
                     sale.OnSale IS NOT NULL
                     OR ISNULL(m.OddsCompanyCount, 0) >= 8
                     OR m.OddsCheckedAt IS NULL
                    )
                AND (
                     m.ExchangeCheckedAt IS NULL
                     OR (m.ExchangeStatus = 1 AND m.MatchTime > SYSDATETIME()
                         AND m.ExchangeCheckedAt < DATEADD(MINUTE,
                             -CASE WHEN sale.OnSale IS NOT NULL THEN @SoldRecheck ELSE @Recheck END,
                             SYSDATETIME()))
                     -- 在售但暂无必发盘：45 分钟后再探，避免被长间隔拖死
                     OR (m.ExchangeStatus = 0 AND m.MatchTime > SYSDATETIME()
                         AND m.ExchangeCheckedAt < DATEADD(MINUTE,
                             -CASE WHEN sale.OnSale IS NOT NULL THEN 45 ELSE @Recheck END,
                             SYSDATETIME()))
                     OR (m.ExchangeStatus = 2 AND m.ExchangeCheckedAt < DATEADD(MINUTE, -15, SYSDATETIME()))
                    )
              ORDER BY CASE WHEN sale.OnSale IS NOT NULL THEN 0 ELSE 1 END,
                       CASE WHEN m.ExchangeCheckedAt IS NULL THEN 0 ELSE 1 END,
                       CASE WHEN ISNULL(m.OddsCompanyCount, 0) >= 8 THEN 0 ELSE 1 END,
                       ISNULL(m.OddsCompanyCount, 0) DESC, m.MatchTime",
            new
            {
                Take = take <= 0 ? 100000 : take,
                From = from,
                To = to,
                Recheck = normalRecheck,
                SoldRecheck = soldRecheck
            });
        return rows.ToList();
    }

    /// <summary>还没查过必发的场，加上上次请求失败的场。已经有快照的不重抓。</summary>
    public async Task<List<MatchBrief>> GetUncheckedForBackfillAsync(int take, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<MatchBrief>(
            @"SELECT TOP (@Take) m.MatchId, m.MatchTime, m.HomeTeamName, m.AwayTeamName,
                     ISNULL(l.Name, N'') AS LeagueName
              FROM dbo.FbMatch m
              LEFT JOIN dbo.FbLeague l ON l.LeagueId = m.LeagueId
              WHERE m.ExchangeCheckedAt IS NULL OR m.ExchangeStatus = 2
              ORDER BY CASE WHEN m.ExchangeCheckedAt IS NULL THEN 0 ELSE 1 END, m.MatchTime",
            new { Take = take <= 0 ? 100000 : take });
        return rows.ToList();
    }

    /// <summary>单场必发快照时间序列。</summary>
    public async Task<List<ExchangeSnapshot>> GetTimelineAsync(long matchId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ExchangeSnapshot>(
            @"SELECT MatchId, CapturedAt, MinutesToKick,
                     EuroHome, EuroDraw, EuroAway, EuroProbHome, EuroProbDraw, EuroProbAway,
                     PriceHome, PriceDraw, PriceAway, VolumeHome, VolumeDraw, VolumeAway,
                     PnlHome, PnlDraw, PnlAway, RatioHome, RatioDraw, RatioAway,
                     BdRatioHome, BdRatioDraw, BdRatioAway,
                     BfIdxHome, BfIdxDraw, BfIdxAway, HotIdxHome, HotIdxDraw, HotIdxAway,
                     PnlIdxHome, PnlIdxDraw, PnlIdxAway, TotalVolume, BigTotal,
                     BigHome, BigDraw, BigAway, TipScale, TipLean
              FROM dbo.FbExchange WHERE MatchId = @matchId ORDER BY CapturedAt", new { matchId });
        return rows.ToList();
    }

    /// <summary>必发覆盖率概览。</summary>
    public async Task<dynamic?> GetCoverageAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync(
            @"SELECT
                (SELECT COUNT(*) FROM dbo.FbMatch) AS MatchTotal,
                (SELECT COUNT(*) FROM dbo.FbMatch WHERE ExchangeStatus = 1) AS WithExchange,
                (SELECT COUNT(*) FROM dbo.FbMatch WHERE ExchangeStatus = 0) AS NoExchange,
                (SELECT COUNT(*) FROM dbo.FbMatch WHERE ExchangeStatus = 2) AS Failed,
                (SELECT COUNT(*) FROM dbo.FbMatch WHERE ExchangeCheckedAt IS NULL) AS Unchecked,
                (SELECT COUNT(*) FROM dbo.FbExchange) AS Snapshots,
                (SELECT COUNT(*) FROM dbo.FbExchangeTrade) AS Trades,
                (SELECT CAST(SUM(TotalVolume) AS FLOAT) FROM dbo.FbExchange WHERE CapturedAt = (SELECT MAX(CapturedAt) FROM dbo.FbExchange)) AS LatestTotalVolume,
                (SELECT COUNT(*) FROM dbo.FbOddsHistory) AS OddsHistoryPoints,
                (SELECT COUNT(DISTINCT MatchId) FROM dbo.FbOddsHistory) AS OddsHistoryMatches");
    }
}
