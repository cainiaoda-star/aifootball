using System.Data;
using AiFootBall.Core.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AiFootBall.Data;

/// <summary>赔率仓储：批量落库、覆盖统计、单场明细。</summary>
public sealed class OddsRepository
{
    private readonly SqlConnectionFactory _factory;

    public OddsRepository(SqlConnectionFactory factory) => _factory = factory;

    private const string CreateStageSql = @"
IF OBJECT_ID('tempdb..#OddsStage') IS NOT NULL DROP TABLE #OddsStage;
CREATE TABLE #OddsStage
(
    MatchId BIGINT NOT NULL, CompanyId INT NOT NULL, Company NVARCHAR(40) NOT NULL,
    OddsType CHAR(2) NOT NULL, IsOpening BIT NOT NULL,
    HomeWin DECIMAL(8,3) NULL, Draw DECIMAL(8,3) NULL, AwayWin DECIMAL(8,3) NULL,
    ReturnRate DECIMAL(6,2) NULL,
    KellyWin DECIMAL(8,3) NULL, KellyDraw DECIMAL(8,3) NULL, KellyLoss DECIMAL(8,3) NULL,
    Handicap DECIMAL(6,3) NULL, HomeWater DECIMAL(6,3) NULL, AwayWater DECIMAL(6,3) NULL,
    TotalLine DECIMAL(6,3) NULL, OverWater DECIMAL(6,3) NULL, UnderWater DECIMAL(6,3) NULL
);
IF OBJECT_ID('tempdb..#OddsMark') IS NOT NULL DROP TABLE #OddsMark;
CREATE TABLE #OddsMark (MatchId BIGINT NOT NULL PRIMARY KEY, OddsStatus TINYINT NOT NULL, CompanyCount SMALLINT NOT NULL);";

    private const string MergeOddsSql = @"
MERGE dbo.FbOdds AS t
USING #OddsStage AS s
   ON t.MatchId = s.MatchId AND t.CompanyId = s.CompanyId
  AND t.OddsType = s.OddsType AND t.IsOpening = s.IsOpening
WHEN MATCHED THEN UPDATE SET
    Company = s.Company, HomeWin = s.HomeWin, Draw = s.Draw, AwayWin = s.AwayWin,
    ReturnRate = s.ReturnRate, KellyWin = s.KellyWin, KellyDraw = s.KellyDraw, KellyLoss = s.KellyLoss,
    Handicap = s.Handicap, HomeWater = s.HomeWater, AwayWater = s.AwayWater,
    TotalLine = s.TotalLine, OverWater = s.OverWater, UnderWater = s.UnderWater,
    CapturedAt = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT
    (MatchId, CompanyId, Company, OddsType, IsOpening,
     HomeWin, Draw, AwayWin, ReturnRate, KellyWin, KellyDraw, KellyLoss,
     Handicap, HomeWater, AwayWater, TotalLine, OverWater, UnderWater, CapturedAt)
    VALUES (s.MatchId, s.CompanyId, s.Company, s.OddsType, s.IsOpening,
            s.HomeWin, s.Draw, s.AwayWin, s.ReturnRate, s.KellyWin, s.KellyDraw, s.KellyLoss,
            s.Handicap, s.HomeWater, s.AwayWater, s.TotalLine, s.OverWater, s.UnderWater, SYSDATETIME());

UPDATE m SET m.OddsCheckedAt = SYSDATETIME(), m.OddsStatus = k.OddsStatus, m.OddsCompanyCount = k.CompanyCount
FROM dbo.FbMatch m JOIN #OddsMark k ON k.MatchId = m.MatchId;";

    /// <summary>
    /// 把本次抓到的「即时盘」写成时间序列点。
    /// 只保留平均与几家主流公司，且只对开赛前 30 天 ~ 开赛后 6 小时的场次采样，避免历史回补污染轨迹。
    /// </summary>
    private const string HistorySql = @"
INSERT INTO dbo.FbOddsHistory
    (MatchId, CapturedAt, MinutesToKick, Phase, OddsType, CompanyId, Company,
     HomeWin, Draw, AwayWin, ReturnRate, KellyWin, KellyDraw, KellyLoss,
     Handicap, HomeWater, AwayWater, TotalLine, OverWater, UnderWater)
SELECT s.MatchId, @SampleAt,
       DATEDIFF(MINUTE, @SampleAt, m.MatchTime),
       CASE WHEN m.MatchTime <= @SampleAt THEN 2
            WHEN DATEDIFF(MINUTE, @SampleAt, m.MatchTime) <= 90 THEN 1 ELSE 0 END,
       s.OddsType, s.CompanyId, s.Company,
       s.HomeWin, s.Draw, s.AwayWin, s.ReturnRate, s.KellyWin, s.KellyDraw, s.KellyLoss,
       s.Handicap, s.HomeWater, s.AwayWater, s.TotalLine, s.OverWater, s.UnderWater
FROM #OddsStage s
JOIN dbo.FbMatch m ON m.MatchId = s.MatchId
WHERE s.IsOpening = 0
  AND s.CompanyId IN (0, 2, 3, 5, 6, 12, 280, 293)
  AND m.MatchTime >= DATEADD(HOUR, -6, @SampleAt)
  AND m.MatchTime <= DATEADD(DAY, 30, @SampleAt)
  AND NOT EXISTS (SELECT 1 FROM dbo.FbOddsHistory h
                  WHERE h.MatchId = s.MatchId AND h.CompanyId = s.CompanyId
                    AND h.OddsType = s.OddsType AND h.CapturedAt = @SampleAt);

UPDATE m SET m.OddsSampledAt = @SampleAt, m.OddsSampleCount = ISNULL(m.OddsSampleCount, 0) + 1
FROM dbo.FbMatch m JOIN #OddsMark k ON k.MatchId = m.MatchId;";

    /// <summary>批量写入一批赛事的赔率（覆盖式更新初盘/终盘）。</summary>
    /// <param name="sampleAt">若非空，则把本次「即时盘」作为时间序列点写入 FbOddsHistory。</param>
    public async Task<int> UpsertAsync(IReadOnlyList<MatchOddsResult> results, DateTime? sampleAt = null,
        CancellationToken ct = default)
    {
        var rows = results.SelectMany(r => r.Odds).Where(o => o.HasValue).ToList();
        if (results.Count == 0) return 0;

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(CreateStageSql, transaction: tx);

            var oddsTable = BuildOddsTable(rows);
            if (oddsTable.Rows.Count > 0)
            {
                using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
                {
                    DestinationTableName = "#OddsStage",
                    BatchSize = 5_000,
                    BulkCopyTimeout = 120
                };
                bulk.ColumnMappings.Add("MatchId", "MatchId");
                bulk.ColumnMappings.Add("CompanyId", "CompanyId");
                bulk.ColumnMappings.Add("Company", "Company");
                bulk.ColumnMappings.Add("OddsType", "OddsType");
                bulk.ColumnMappings.Add("IsOpening", "IsOpening");
                bulk.ColumnMappings.Add("HomeWin", "HomeWin");
                bulk.ColumnMappings.Add("Draw", "Draw");
                bulk.ColumnMappings.Add("AwayWin", "AwayWin");
                bulk.ColumnMappings.Add("ReturnRate", "ReturnRate");
                bulk.ColumnMappings.Add("KellyWin", "KellyWin");
                bulk.ColumnMappings.Add("KellyDraw", "KellyDraw");
                bulk.ColumnMappings.Add("KellyLoss", "KellyLoss");
                bulk.ColumnMappings.Add("Handicap", "Handicap");
                bulk.ColumnMappings.Add("HomeWater", "HomeWater");
                bulk.ColumnMappings.Add("AwayWater", "AwayWater");
                bulk.ColumnMappings.Add("TotalLine", "TotalLine");
                bulk.ColumnMappings.Add("OverWater", "OverWater");
                bulk.ColumnMappings.Add("UnderWater", "UnderWater");
                await bulk.WriteToServerAsync(oddsTable, ct);
            }

            var markTable = BuildMarkTable(results);
            using (var bulk2 = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
                   {
                       DestinationTableName = "#OddsMark",
                       BulkCopyTimeout = 120
                   })
            {
                await bulk2.WriteToServerAsync(markTable, ct);
            }

            await conn.ExecuteAsync(MergeOddsSql, transaction: tx, commandTimeout: 180);
            if (sampleAt.HasValue)
                await conn.ExecuteAsync(HistorySql, new { SampleAt = sampleAt.Value },
                    transaction: tx, commandTimeout: 180);
            await tx.CommitAsync(ct);
            return rows.Count;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static DataTable BuildOddsTable(IEnumerable<OddsRecord> rows)
    {
        var dt = new DataTable();
        dt.Columns.Add("MatchId", typeof(long));
        dt.Columns.Add("CompanyId", typeof(int));
        dt.Columns.Add("Company", typeof(string));
        dt.Columns.Add("OddsType", typeof(string));
        dt.Columns.Add("IsOpening", typeof(bool));
        foreach (var c in new[] { "HomeWin", "Draw", "AwayWin", "ReturnRate", "KellyWin", "KellyDraw",
                                  "KellyLoss", "Handicap", "HomeWater", "AwayWater", "TotalLine",
                                  "OverWater", "UnderWater" })
            dt.Columns.Add(c, typeof(decimal));

        foreach (var r in rows)
        {
            dt.Rows.Add(
                r.MatchId, r.CompanyId, r.Company, r.OddsType, r.IsOpening,
                (object?)r.HomeWin ?? DBNull.Value, (object?)r.Draw ?? DBNull.Value,
                (object?)r.AwayWin ?? DBNull.Value, (object?)r.ReturnRate ?? DBNull.Value,
                (object?)r.KellyWin ?? DBNull.Value, (object?)r.KellyDraw ?? DBNull.Value,
                (object?)r.KellyLoss ?? DBNull.Value, (object?)r.Handicap ?? DBNull.Value,
                (object?)r.HomeWater ?? DBNull.Value, (object?)r.AwayWater ?? DBNull.Value,
                (object?)r.TotalLine ?? DBNull.Value, (object?)r.OverWater ?? DBNull.Value,
                (object?)r.UnderWater ?? DBNull.Value);
        }
        return dt;
    }

    private static DataTable BuildMarkTable(IEnumerable<MatchOddsResult> results)
    {
        var dt = new DataTable();
        dt.Columns.Add("MatchId", typeof(long));
        dt.Columns.Add("OddsStatus", typeof(byte));
        dt.Columns.Add("CompanyCount", typeof(short));
        foreach (var r in results)
            dt.Rows.Add(r.MatchId, (byte)r.Status, (short)Math.Min(r.CompanyCount, short.MaxValue));
        return dt;
    }

    /// <summary>取需要采集赔率的赛事。</summary>
    public async Task<List<MatchBrief>> GetMatchIdsForOddsAsync(DateTime from, DateTime to, bool onlyMissing,
        int take, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<MatchBrief>(@"
SELECT TOP (@Take) m.MatchId, m.MatchTime, COALESCE(l.Name, m.LeagueName, N'未知') AS LeagueName,
       m.HomeTeamName, m.AwayTeamName, m.Status, m.OddsStatus, m.OddsCheckedAt, m.OddsCompanyCount
FROM dbo.FbMatch m
LEFT JOIN dbo.FbLeague l ON l.LeagueId = m.LeagueId
WHERE m.MatchTime >= @From AND m.MatchTime < @To
  AND (@OnlyMissing = 0
       OR m.OddsCheckedAt IS NULL
       OR (m.OddsStatus IN (0, 2) AND m.OddsCheckedAt < DATEADD(DAY, -1, SYSDATETIME())))
ORDER BY m.MatchTime", new
        {
            Take = take <= 0 ? 100000 : take,
            From = from,
            To = to,
            OnlyMissing = onlyMissing
        });
        return rows.ToList();
    }

    /// <summary>
    /// 取需要做赔率轨迹采样的赛事。
    /// 时间窗内的场次 + 当前在售受注场（竞彩/北单/胜负彩），避免窗口太短漏掉周六日精选。
    /// 首次采样（OddsSampledAt IS NULL）不设公司数门槛；之后优先采多家公司报价的场次。
    /// </summary>
    public async Task<List<MatchBrief>> GetMatchesForSamplingAsync(DateTime from, DateTime to, int take,
        int recheckMinutes, int minCompanies = 8, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<MatchBrief>(@"
SELECT TOP (@Take) m.MatchId, m.MatchTime, COALESCE(l.Name, m.LeagueName, N'未知') AS LeagueName,
       m.HomeTeamName, m.AwayTeamName, m.Status, m.OddsStatus, m.OddsCheckedAt, m.OddsCompanyCount
FROM dbo.FbMatch m
LEFT JOIN dbo.FbLeague l ON l.LeagueId = m.LeagueId
WHERE m.MatchTime > DATEADD(HOUR, -2, SYSDATETIME())
  AND (
        (m.MatchTime >= @From AND m.MatchTime <= @To)
        OR EXISTS (
            SELECT 1 FROM dbo.BettingCard c
            WHERE c.MatchId = m.MatchId
              AND c.CapturedAt = (SELECT MAX(CapturedAt) FROM dbo.BettingCard b WHERE b.Board = c.Board)
        )
      )
  AND (
        m.OddsSampledAt IS NULL
        OR (ISNULL(m.OddsCompanyCount, 0) >= @MinCompanies
            AND m.OddsSampledAt < DATEADD(MINUTE, -@Recheck, SYSDATETIME()))
        OR (
            EXISTS (
                SELECT 1 FROM dbo.BettingCard c
                WHERE c.MatchId = m.MatchId
                  AND c.CapturedAt = (SELECT MAX(CapturedAt) FROM dbo.BettingCard b WHERE b.Board = c.Board)
            )
            AND m.OddsSampledAt < DATEADD(MINUTE, -@Recheck, SYSDATETIME())
        )
      )
ORDER BY CASE
           WHEN EXISTS (
             SELECT 1 FROM dbo.BettingCard c
             WHERE c.MatchId = m.MatchId
               AND c.CapturedAt = (SELECT MAX(CapturedAt) FROM dbo.BettingCard b WHERE b.Board = c.Board)
           ) THEN 0 ELSE 1 END,
         CASE WHEN m.OddsSampledAt IS NULL THEN 0 ELSE 1 END,
         ISNULL(m.OddsCompanyCount, 0) DESC, m.MatchTime", new
        {
            Take = take <= 0 ? 100000 : take,
            From = from,
            To = to,
            Recheck = recheckMinutes,
            MinCompanies = minCompanies
        });
        return rows.ToList();
    }

    public async Task<List<FbOddsView>> GetOddsAsync(long matchId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<FbOddsView>(@"
SELECT CompanyId, Company, OddsType, IsOpening, HomeWin, Draw, AwayWin, ReturnRate,
       KellyWin, KellyDraw, KellyLoss, Handicap, HomeWater, AwayWater, TotalLine, OverWater, UnderWater
FROM dbo.FbOdds WHERE MatchId = @MatchId
ORDER BY CASE OddsType WHEN 'EU' THEN 0 WHEN 'AH' THEN 1 ELSE 2 END, IsOpening DESC,
         CASE WHEN CompanyId = 0 THEN 0 ELSE 1 END, CompanyId",
            new { MatchId = matchId });
        return rows.ToList();
    }

    /// <summary>赔率覆盖情况统计。</summary>
    public async Task<dynamic?> GetCoverageAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync(@"
SELECT (SELECT COUNT(*) FROM dbo.FbMatch)                                     AS MatchTotal,
       (SELECT COUNT(*) FROM dbo.FbMatch WHERE OddsCheckedAt IS NULL)          AS Unchecked,
       (SELECT COUNT(*) FROM dbo.FbMatch WHERE OddsStatus = 1)                 AS WithOdds,
       (SELECT COUNT(*) FROM dbo.FbMatch WHERE OddsStatus = 0)                 AS NoOdds,
       (SELECT COUNT(*) FROM dbo.FbMatch WHERE OddsStatus = 2)                 AS Failed,
       (SELECT COUNT(*) FROM dbo.FbOdds)                                       AS OddsRows,
       (SELECT COUNT(DISTINCT MatchId) FROM dbo.FbOdds)                        AS OddsMatches,
       (SELECT COUNT(DISTINCT CompanyId) FROM dbo.FbOdds WHERE CompanyId > 0)  AS Companies,
       (SELECT COUNT(*) FROM dbo.FbOdds WHERE OddsType = 'EU')                 AS EuRows,
       (SELECT COUNT(*) FROM dbo.FbOdds WHERE OddsType = 'AH')                 AS AhRows,
       (SELECT COUNT(*) FROM dbo.FbOdds WHERE OddsType = 'OU')                 AS OuRows,
       (SELECT COUNT(*) FROM dbo.FbMatch WHERE OddsStatus = 1 AND Status = 3)  AS FinishedWithOdds");
        return rows.FirstOrDefault();
    }

    /// <summary>
    /// 初盘 → 终盘变化方向与赛果的关系（只在已完场且有赔率的赛事上统计）。
    /// </summary>
    public async Task<dynamic?> GetSignalStatsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var eu = await conn.QueryAsync(@"
WITH m AS (
    SELECT MatchId,
           AVG(CASE WHEN IsOpening = 1 THEN HomeWin END) AS OpenHome,
           AVG(CASE WHEN IsOpening = 0 THEN HomeWin END) AS CloseHome
    FROM dbo.FbOdds WHERE OddsType = 'EU' AND CompanyId > 0
    GROUP BY MatchId
)
SELECT CASE WHEN m.CloseHome < m.OpenHome - 0.02 THEN N'主胜赔率下降'
            WHEN m.CloseHome > m.OpenHome + 0.02 THEN N'主胜赔率上升'
            ELSE N'基本持平' END AS Direction,
       COUNT(*) AS Matches,
       CAST(ROUND(AVG(CASE WHEN f.ResultSpf = '3' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS HomeWinPct,
       CAST(ROUND(AVG(CASE WHEN f.ResultSpf = '1' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS DrawPct,
       CAST(ROUND(AVG(CASE WHEN f.ResultSpf = '0' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS AwayWinPct
FROM m
JOIN dbo.FbMatch f ON f.MatchId = m.MatchId AND f.Status = 3
WHERE m.OpenHome IS NOT NULL AND m.CloseHome IS NOT NULL
GROUP BY CASE WHEN m.CloseHome < m.OpenHome - 0.02 THEN N'主胜赔率下降'
              WHEN m.CloseHome > m.OpenHome + 0.02 THEN N'主胜赔率上升'
              ELSE N'基本持平' END
ORDER BY COUNT(*) DESC");

        var ah = await conn.QueryAsync(@"
WITH m AS (
    SELECT MatchId,
           AVG(CASE WHEN IsOpening = 1 THEN Handicap END) AS OpenHcp,
           AVG(CASE WHEN IsOpening = 0 THEN Handicap END) AS CloseHcp
    FROM dbo.FbOdds WHERE OddsType = 'AH' AND CompanyId > 0
    GROUP BY MatchId
)
SELECT CASE WHEN m.CloseHcp < m.OpenHcp - 0.2 THEN N'盘口加深（更看好主队）'
            WHEN m.CloseHcp > m.OpenHcp + 0.2 THEN N'盘口变浅'
            ELSE N'盘口稳定' END AS Direction,
       COUNT(*) AS Matches,
       CAST(ROUND(AVG(CASE WHEN f.HomeScore + m.CloseHcp - f.AwayScore > 0 THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS HomeCoverPct,
       CAST(ROUND(AVG(CASE WHEN f.ResultSpf = '3' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS HomeWinPct
FROM m
JOIN dbo.FbMatch f ON f.MatchId = m.MatchId AND f.Status = 3
WHERE m.OpenHcp IS NOT NULL AND m.CloseHcp IS NOT NULL
GROUP BY CASE WHEN m.CloseHcp < m.OpenHcp - 0.2 THEN N'盘口加深（更看好主队）'
              WHEN m.CloseHcp > m.OpenHcp + 0.2 THEN N'盘口变浅'
              ELSE N'盘口稳定' END
ORDER BY COUNT(*) DESC");

        return new { eu = eu.ToList(), ah = ah.ToList() };
    }

    /// <summary>初盘 → 终盘的平均变化（按玩法聚合），用于验证数据有效性。</summary>
    public async Task<List<dynamic>> GetMovementStatsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync(@"
WITH o AS (
    SELECT MatchId, CompanyId, OddsType,
           MAX(CASE WHEN IsOpening = 1 THEN HomeWin END)  AS OpenHome,
           MAX(CASE WHEN IsOpening = 1 THEN Draw END)     AS OpenDraw,
           MAX(CASE WHEN IsOpening = 1 THEN AwayWin END)  AS OpenAway,
           MAX(CASE WHEN IsOpening = 0 THEN HomeWin END)  AS CloseHome,
           MAX(CASE WHEN IsOpening = 0 THEN Draw END)     AS CloseDraw,
           MAX(CASE WHEN IsOpening = 0 THEN AwayWin END)  AS CloseAway,
           MAX(CASE WHEN IsOpening = 1 THEN Handicap END) AS OpenHcp,
           MAX(CASE WHEN IsOpening = 0 THEN Handicap END) AS CloseHcp,
           MAX(CASE WHEN IsOpening = 1 THEN TotalLine END) AS OpenLine,
           MAX(CASE WHEN IsOpening = 0 THEN TotalLine END) AS CloseLine
    FROM dbo.FbOdds WHERE CompanyId > 0
    GROUP BY MatchId, CompanyId, OddsType
)
SELECT OddsType,
       COUNT(*) AS Samples,
       CAST(ROUND(AVG(CASE WHEN OddsType = 'EU' AND CloseHome <> OpenHome THEN 1.0 ELSE 0 END) * 100, 2) AS FLOAT) AS HomeChangedPct,
       CAST(ROUND(AVG(CASE WHEN OddsType = 'AH' AND CloseHcp <> OpenHcp THEN 1.0 ELSE 0 END) * 100, 2) AS FLOAT) AS LineChangedPct,
       CAST(ROUND(AVG(CASE WHEN OddsType = 'OU' AND CloseLine <> OpenLine THEN 1.0 ELSE 0 END) * 100, 2) AS FLOAT) AS TotalChangedPct,
       CAST(ROUND(AVG(CloseHome - OpenHome), 4) AS FLOAT) AS AvgHomeDelta
FROM o GROUP BY OddsType");
        return rows.ToList();
    }
}
