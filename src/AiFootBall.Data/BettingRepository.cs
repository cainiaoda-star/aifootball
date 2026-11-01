using AiFootBall.Core.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AiFootBall.Data;

/// <summary>受注场次 + 推荐记录仓储</summary>
public sealed class BettingRepository
{
    private readonly SqlConnectionFactory _factory;

    public BettingRepository(SqlConnectionFactory factory) => _factory = factory;

    /* ---------------- 受注场次 ---------------- */

    /// <summary>批量写入受注场次（MERGE 幂等）</summary>
    public async Task<int> UpsertCardsAsync(IEnumerable<BettingCardRecord> cards, CancellationToken ct = default)
    {
        var list = cards.ToList();
        if (list.Count == 0) return 0;

        var capturedAt = DateTime.Now;
        await using var conn = await _factory.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            @"MERGE dbo.BettingCard AS t
              USING (VALUES (@board,@expect,@no,@mid,@lg,@h,@a,@time,@hcp,@sh,@sd,@sa,@single,@cap)) 
                AS s(Board, Expect, MatchNo, MatchId, LeagueName, HomeTeam, AwayTeam, MatchTime, Handicap, SpfHome, SpfDraw, SpfAway, IsSingle, CapturedAt)
              ON t.Board = s.Board AND ISNULL(t.Expect,'') = ISNULL(s.Expect,'') AND t.MatchNo = s.MatchNo
              WHEN MATCHED THEN UPDATE SET
                MatchId = s.MatchId, LeagueName = s.LeagueName, HomeTeam = s.HomeTeam, AwayTeam = s.AwayTeam,
                MatchTime = s.MatchTime, Handicap = s.Handicap, SpfHome = s.SpfHome, SpfDraw = s.SpfDraw, SpfAway = s.SpfAway,
                IsSingle = s.IsSingle, CapturedAt = s.CapturedAt
              WHEN NOT MATCHED THEN INSERT (Board, Expect, MatchNo, MatchId, LeagueName, HomeTeam, AwayTeam, MatchTime, Handicap, SpfHome, SpfDraw, SpfAway, IsSingle, CapturedAt)
                VALUES (s.Board, s.Expect, s.MatchNo, s.MatchId, s.LeagueName, s.HomeTeam, s.AwayTeam, s.MatchTime, s.Handicap, s.SpfHome, s.SpfDraw, s.SpfAway, s.IsSingle, s.CapturedAt);",
            conn);

        var affected = 0;
        foreach (var c in list)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@board", c.Board);
            cmd.Parameters.AddWithValue("@expect", (object?)c.Expect ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@no", c.MatchNo);
            cmd.Parameters.AddWithValue("@mid", (object?)c.MatchId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lg", (object?)c.LeagueName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@h", (object?)c.HomeTeam ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@a", (object?)c.AwayTeam ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@time", (object?)c.MatchTime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@hcp", (object?)c.Handicap ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sh", (object?)c.SpfHome ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sd", (object?)c.SpfDraw ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sa", (object?)c.SpfAway ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@single", c.IsSingle);
            cmd.Parameters.AddWithValue("@cap", capturedAt);
            affected += await cmd.ExecuteNonQueryAsync(ct);
        }
        return affected;
    }

    /// <summary>取某板块最近一整批受注场次。同一批写入共用一个 CapturedAt，避免按 5 分钟窗口把慢采集截断。</summary>
    public async Task<List<BettingCardRecord>> GetCardsAsync(string board, string? expect = null, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<BettingCardRecord>(new CommandDefinition(
            @"SELECT * FROM dbo.BettingCard
              WHERE Board = @b
                AND CapturedAt = (SELECT MAX(CapturedAt) FROM dbo.BettingCard WHERE Board = @b)
                AND (@e IS NULL OR Expect = @e)
              ORDER BY CardId",
            new { b = board, e = expect }, cancellationToken: ct))).ToList();
    }

    /// <summary>某一天开赛的受注卡。同一场保留最后一次采集，不限「最新一整批」。</summary>
    public async Task<List<BettingCardRecord>> GetCardsByKickoffAsync(
        string board, DateTime day, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<BettingCardRecord>(new CommandDefinition(
            @"WITH cte AS (
                  SELECT c.CardId, c.Board, c.Expect, c.MatchNo, c.MatchId, c.LeagueName,
                         c.HomeTeam, c.AwayTeam, COALESCE(c.MatchTime, m.MatchTime) AS MatchTime,
                         c.Handicap, c.SpfHome, c.SpfDraw, c.SpfAway, c.IsSingle, c.CapturedAt,
                         ROW_NUMBER() OVER (
                             PARTITION BY COALESCE(CONVERT(varchar(20), c.MatchId), c.MatchNo)
                             ORDER BY c.CapturedAt DESC, c.CardId DESC) AS rn
                  FROM dbo.BettingCard c
                  LEFT JOIN dbo.FbMatch m ON m.MatchId = c.MatchId
                  WHERE c.Board = @b
                    AND COALESCE(c.MatchTime, m.MatchTime) >= @from
                    AND COALESCE(c.MatchTime, m.MatchTime) < @to
              )
              SELECT CardId, Board, Expect, MatchNo, MatchId, LeagueName, HomeTeam, AwayTeam,
                     MatchTime, Handicap, SpfHome, SpfDraw, SpfAway, IsSingle, CapturedAt
              FROM cte WHERE rn = 1
              ORDER BY MatchTime, MatchNo",
            new { b = board, from = day.Date, to = day.Date.AddDays(1) }, cancellationToken: ct))).ToList();
    }

    /* ---------------- 推荐信号数据 ---------------- */

    /// <summary>按 fid 批量取欧赔主胜 初盘→终盘 变化（平均值 CompanyId=0）</summary>
    public async Task<Dictionary<long, decimal?>> GetHomeOddsDeltaAsync(IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var all = await GetEuOddsDeltaAsync(matchIds, ct);
        return all.ToDictionary(kv => kv.Key, kv => kv.Value.Home);
    }

    /// <summary>按 fid 批量取欧赔主胜/平局 初盘→终盘变化（平均值 CompanyId=0）</summary>
    public async Task<Dictionary<long, (decimal? Home, decimal? Draw)>> GetEuOddsDeltaAsync(
        IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, (decimal?, decimal?)>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<EuDeltaRow>(new CommandDefinition(
                @"SELECT MatchId,
                         MAX(CASE WHEN IsOpening = 0 THEN HomeWin END)
                       - MAX(CASE WHEN IsOpening = 1 THEN HomeWin END) AS HomeDelta,
                         MAX(CASE WHEN IsOpening = 0 THEN Draw END)
                       - MAX(CASE WHEN IsOpening = 1 THEN Draw END) AS DrawDelta
                  FROM dbo.FbOdds
                  WHERE MatchId IN @ids AND OddsType = 'EU' AND CompanyId = 0
                  GROUP BY MatchId",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var row in rows)
                result[row.MatchId] = (row.HomeDelta, row.DrawDelta);
        }
        return result;
    }

    /// <summary>按 fid 批量取欧赔终盘主/平/客（平均值 CompanyId=0），受注场次没有 SP 时用来判断冷热。</summary>
    public async Task<Dictionary<long, (decimal? Home, decimal? Draw, decimal? Away)>> GetClosingEuAsync(IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, (decimal?, decimal?, decimal?)>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<ClosingEuRow>(new CommandDefinition(
                @"SELECT MatchId,
                         MAX(CASE WHEN IsOpening = 0 THEN HomeWin END) AS HomeWin,
                         MAX(CASE WHEN IsOpening = 0 THEN Draw END) AS Draw,
                         MAX(CASE WHEN IsOpening = 0 THEN AwayWin END) AS AwayWin
                  FROM dbo.FbOdds
                  WHERE MatchId IN @ids AND OddsType = 'EU' AND CompanyId = 0
                  GROUP BY MatchId",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var row in rows)
                result[row.MatchId] = (row.HomeWin, row.Draw, row.AwayWin);
        }
        return result;
    }

    /// <summary>亚盘初盘和终盘。终盘比初盘更小表示更看好主队。</summary>
    public async Task<Dictionary<long, (decimal? Open, decimal? Close)>> GetAsianMoveAsync(IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, (decimal?, decimal?)>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<AsianMoveRow>(new CommandDefinition(
                @"SELECT MatchId,
                         MAX(CASE WHEN IsOpening = 1 THEN Handicap END) AS OpenHcp,
                         MAX(CASE WHEN IsOpening = 0 THEN Handicap END) AS CloseHcp
                  FROM dbo.FbOdds
                  WHERE MatchId IN @ids AND OddsType = 'AH' AND CompanyId = 0
                  GROUP BY MatchId",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var row in rows)
                result[row.MatchId] = (row.OpenHcp, row.CloseHcp);
        }
        return result;
    }

    /// <summary>完场且有终盘欧赔的样本，供命中率模型按时间切分。</summary>
    public async Task<List<HitTrainingRow>> GetHitTrainingRowsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<HitTrainingRow>(new CommandDefinition(
            @"SELECT f.MatchId, f.MatchTime, f.LeagueName, f.ResultSpf AS Result,
                     eu.HomeWin AS HomeOdds, eu.HomeOpen AS HomeOddsOpen,
                     eu.Draw AS DrawOdds, eu.AwayWin AS AwayOdds,
                     ah.OpenHcp AS AhOpen, ah.CloseHcp AS AhClose,
                     ex.RatioHome, ex.RatioDraw, ex.RatioAway
              FROM dbo.FbMatch f
              JOIN (
                  SELECT MatchId,
                         MAX(CASE WHEN IsOpening = 1 THEN HomeWin END) AS HomeOpen,
                         MAX(CASE WHEN IsOpening = 0 THEN HomeWin END) AS HomeWin,
                         MAX(CASE WHEN IsOpening = 0 THEN Draw END) AS Draw,
                         MAX(CASE WHEN IsOpening = 0 THEN AwayWin END) AS AwayWin
                  FROM dbo.FbOdds
                  WHERE OddsType = 'EU' AND CompanyId = 0
                  GROUP BY MatchId
              ) eu ON eu.MatchId = f.MatchId
              LEFT JOIN (
                  SELECT MatchId,
                         MAX(CASE WHEN IsOpening = 1 THEN Handicap END) AS OpenHcp,
                         MAX(CASE WHEN IsOpening = 0 THEN Handicap END) AS CloseHcp
                  FROM dbo.FbOdds
                  WHERE OddsType = 'AH' AND CompanyId = 0
                  GROUP BY MatchId
              ) ah ON ah.MatchId = f.MatchId
              LEFT JOIN (
                  SELECT MatchId, RatioHome, RatioDraw, RatioAway
                  FROM (
                      SELECT MatchId, RatioHome, RatioDraw, RatioAway,
                             ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt DESC) AS rn
                      FROM dbo.FbExchange
                  ) t
                  WHERE rn = 1
              ) ex ON ex.MatchId = f.MatchId
              WHERE f.Status = 3
                AND f.ResultSpf IN ('3','1','0')
                AND eu.HomeWin > 1 AND eu.Draw > 1 AND eu.AwayWin > 1",
            cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>近 18 个月完场比分，供球队攻防估计。</summary>
    public async Task<List<ScoreSample>> GetScoreSamplesAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ScoreSample>(new CommandDefinition(
            @"SELECT f.LeagueName, f.HomeTeamName AS HomeTeam, f.AwayTeamName AS AwayTeam,
                     f.HomeScore, f.AwayScore
              FROM dbo.FbMatch f
              WHERE f.Status = 3
                AND f.HomeScore IS NOT NULL AND f.AwayScore IS NOT NULL
                AND f.MatchTime >= DATEADD(month, -18, GETDATE())",
            cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// 尾盘主胜变化：最近采样 − 至少 4 小时前的采样（无则用最早点）。
    /// 正值=尾盘抬高主胜赔（看淡主）；负值=尾盘继续压主。
    /// </summary>
    public async Task<Dictionary<long, decimal?>> GetLateHomeSteamAsync(
        IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, decimal?>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<(long MatchId, decimal? LateDelta)>(new CommandDefinition(
                @"WITH pts AS (
                      SELECT MatchId, HomeWin, CapturedAt,
                             ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt DESC) AS rd,
                             COUNT(*) OVER (PARTITION BY MatchId) AS cnt
                      FROM dbo.FbOddsHistory
                      WHERE MatchId IN @ids AND OddsType = 'EU' AND CompanyId = 0 AND HomeWin > 1
                  ),
                  latest AS (SELECT MatchId, HomeWin, CapturedAt, cnt FROM pts WHERE rd = 1 AND cnt >= 2),
                  earlier AS (
                      SELECT p.MatchId, p.HomeWin,
                             ROW_NUMBER() OVER (PARTITION BY p.MatchId ORDER BY p.CapturedAt DESC) AS rn
                      FROM dbo.FbOddsHistory p
                      INNER JOIN latest l ON l.MatchId = p.MatchId
                      WHERE p.OddsType = 'EU' AND p.CompanyId = 0 AND p.HomeWin > 1
                        AND p.CapturedAt <= DATEADD(HOUR, -4, l.CapturedAt)
                  )
                  SELECT l.MatchId,
                         l.HomeWin - e.HomeWin AS LateDelta
                  FROM latest l
                  INNER JOIN earlier e ON e.MatchId = l.MatchId AND e.rn = 1",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var row in rows)
                result[row.MatchId] = row.LateDelta;
        }
        return result;
    }

    /// <summary>主/客近 5 场完场积分（胜3平1），按本场开赛时间之前统计。</summary>
    public async Task<Dictionary<long, (int? HomePts, int? AwayPts)>> GetTeamFormPointsAsync(
        IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, (int?, int?)>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<(long MatchId, int? HomePts, int? AwayPts)>(new CommandDefinition(
                @"SELECT m.MatchId,
                         (
                             SELECT SUM(pts) FROM (
                                 SELECT TOP 5 CASE
                                     WHEN x.HomeTeamId = m.HomeTeamId AND x.ResultSpf = '3' THEN 3
                                     WHEN x.AwayTeamId = m.HomeTeamId AND x.ResultSpf = '0' THEN 3
                                     WHEN x.ResultSpf = '1' THEN 1
                                     ELSE 0 END AS pts
                                 FROM dbo.FbMatch x
                                 WHERE x.Status = 3 AND x.ResultSpf IN ('3','1','0')
                                   AND x.MatchTime < m.MatchTime
                                   AND (x.HomeTeamId = m.HomeTeamId OR x.AwayTeamId = m.HomeTeamId)
                                   AND m.HomeTeamId IS NOT NULL
                                 ORDER BY x.MatchTime DESC
                             ) t
                         ) AS HomePts,
                         (
                             SELECT SUM(pts) FROM (
                                 SELECT TOP 5 CASE
                                     WHEN x.HomeTeamId = m.AwayTeamId AND x.ResultSpf = '3' THEN 3
                                     WHEN x.AwayTeamId = m.AwayTeamId AND x.ResultSpf = '0' THEN 3
                                     WHEN x.ResultSpf = '1' THEN 1
                                     ELSE 0 END AS pts
                                 FROM dbo.FbMatch x
                                 WHERE x.Status = 3 AND x.ResultSpf IN ('3','1','0')
                                   AND x.MatchTime < m.MatchTime
                                   AND (x.HomeTeamId = m.AwayTeamId OR x.AwayTeamId = m.AwayTeamId)
                                   AND m.AwayTeamId IS NOT NULL
                                 ORDER BY x.MatchTime DESC
                             ) t
                         ) AS AwayPts
                  FROM dbo.FbMatch m
                  WHERE m.MatchId IN @ids",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var row in rows)
                result[row.MatchId] = (row.HomePts, row.AwayPts);
        }
        return result;
    }

    /// <summary>已结算推荐按方向和水位段汇总，用来把单场概率往真实命中收缩。</summary>
    public async Task<List<HitRateMath.HitBucketCount>> GetHitBucketsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<HitRateMath.HitBucketCount>(new CommandDefinition(
            @"SELECT r.RecResult AS Side,
                     CASE
                         WHEN picked < 1.40 THEN 'lt14'
                         WHEN picked < 2 THEN '14to2'
                         WHEN picked < 3 THEN '2to3'
                         WHEN picked < 5 THEN '3to5'
                         ELSE 'ge5'
                     END AS Band,
                     COUNT(*) AS Total,
                     SUM(CASE WHEN r.Hit = 1 THEN 1 ELSE 0 END) AS Hits
              FROM (
                  SELECT *, ROW_NUMBER() OVER (
                      PARTITION BY Board, MatchId ORDER BY CreatedAt DESC, RecId DESC) AS rn
                  FROM dbo.Recommendation
                  WHERE MatchId IS NOT NULL
              ) r
              JOIN (
                  SELECT MatchId,
                         MAX(CASE WHEN IsOpening = 0 THEN HomeWin END) AS HomeWin,
                         MAX(CASE WHEN IsOpening = 0 THEN Draw END) AS Draw,
                         MAX(CASE WHEN IsOpening = 0 THEN AwayWin END) AS AwayWin
                  FROM dbo.FbOdds
                  WHERE OddsType = 'EU' AND CompanyId = 0
                  GROUP BY MatchId
              ) eu ON eu.MatchId = r.MatchId
              CROSS APPLY (SELECT CASE r.RecResult
                               WHEN '3' THEN eu.HomeWin
                               WHEN '1' THEN eu.Draw
                               ELSE eu.AwayWin END AS picked) p
              WHERE r.rn = 1
                AND r.IsCoverAll = 0
                AND r.RecResult IN ('3','1','0')
                AND r.Hit IS NOT NULL
                AND r.Confidence >= 60
                AND r.Board IN ('jc','bd')
                AND p.picked > 1
              GROUP BY r.RecResult,
                       CASE
                           WHEN picked < 1.40 THEN 'lt14'
                           WHEN picked < 2 THEN '14to2'
                           WHEN picked < 3 THEN '2to3'
                           WHEN picked < 5 THEN '3to5'
                           ELSE 'ge5'
                       END",
            cancellationToken: ct));
        return rows.ToList();
    }

    private sealed class AsianMoveRow
    {
        public long MatchId { get; set; }
        public decimal? OpenHcp { get; set; }
        public decimal? CloseHcp { get; set; }
    }

    private sealed class ClosingEuRow
    {
        public long MatchId { get; set; }
        public decimal? HomeWin { get; set; }
        public decimal? Draw { get; set; }
        public decimal? AwayWin { get; set; }
    }

    private sealed class EuDeltaRow
    {
        public long MatchId { get; set; }
        public decimal? HomeDelta { get; set; }
        public decimal? DrawDelta { get; set; }
    }

    /// <summary>按 fid 批量取必发主/平/客投注占比（最新快照）</summary>
    public async Task<Dictionary<long, (decimal? Home, decimal? Draw, decimal? Away)>> GetExchangeRatioAsync(
        IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, (decimal?, decimal?, decimal?)>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<ExchangeRatio>(new CommandDefinition(
                @"SELECT MatchId, RatioHome, RatioDraw, RatioAway
                  FROM (
                      SELECT MatchId, RatioHome, RatioDraw, RatioAway,
                             ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt DESC) AS rn
                      FROM dbo.FbExchange
                      WHERE MatchId IN @ids
                  ) t
                  WHERE rn = 1",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var row in rows)
                result[row.MatchId] = (row.RatioHome, row.RatioDraw, row.RatioAway);
        }
        return result;
    }

    /// <summary>必发最后一次采样，以及主胜/客胜占比的最高一次。复盘用来看中途有没有摸到极端大热。</summary>
    public async Task<Dictionary<long, (decimal? LastHome, decimal? LastDraw, decimal? LastAway, decimal? MaxHome, decimal? MaxAway)>>
        GetExchangeSpanAsync(IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, (decimal?, decimal?, decimal?, decimal?, decimal?)>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<ExchangeSpanRow>(new CommandDefinition(
                @"SELECT MatchId,
                         MAX(CASE WHEN rn = 1 THEN RatioHome END) AS LastHome,
                         MAX(CASE WHEN rn = 1 THEN RatioDraw END) AS LastDraw,
                         MAX(CASE WHEN rn = 1 THEN RatioAway END) AS LastAway,
                         MAX(RatioHome) AS MaxHome,
                         MAX(RatioAway) AS MaxAway
                  FROM (
                      SELECT MatchId, RatioHome, RatioDraw, RatioAway,
                             ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt DESC) AS rn
                      FROM dbo.FbExchange
                      WHERE MatchId IN @ids
                  ) t
                  GROUP BY MatchId",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var row in rows)
                result[row.MatchId] = (row.LastHome, row.LastDraw, row.LastAway, row.MaxHome, row.MaxAway);
        }
        return result;
    }

    private sealed class ExchangeSpanRow
    {
        public long MatchId { get; set; }
        public decimal? LastHome { get; set; }
        public decimal? LastDraw { get; set; }
        public decimal? LastAway { get; set; }
        public decimal? MaxHome { get; set; }
        public decimal? MaxAway { get; set; }
    }

    private sealed class ExchangeRatio
    {
        public long MatchId { get; set; }
        public decimal? RatioHome { get; set; }
        public decimal? RatioDraw { get; set; }
        public decimal? RatioAway { get; set; }
    }

    /// <summary>按 fid 批量取大小球盘口线（OU 终盘，CompanyId=0）</summary>
    public async Task<Dictionary<long, decimal?>> GetTotalLineAsync(IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, decimal?>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<TotalLineRow>(new CommandDefinition(
                @"SELECT MatchId, TotalLine
                  FROM (
                      SELECT MatchId, TotalLine,
                             ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt DESC) AS rn
                      FROM dbo.FbOdds
                      WHERE MatchId IN @ids AND OddsType = 'OU' AND IsOpening = 0
                        AND CompanyId = 0 AND TotalLine IS NOT NULL
                  ) t
                  WHERE rn = 1",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var row in rows)
                result[row.MatchId] = row.TotalLine;
        }
        return result;
    }

    private sealed class TotalLineRow
    {
        public long MatchId { get; set; }
        public decimal? TotalLine { get; set; }
    }

    /* ---------------- 推荐记录 ---------------- */

    /// <summary>删掉某日某板块全部推荐（重算精选前清场，避免旧「够格」残留）。</summary>
    public async Task DeleteRecommendationsByBoardDateAsync(string board, DateTime recDate, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            @"DELETE FROM dbo.Recommendation WHERE Board = @b AND RecDate = @rd",
            new { b = board, rd = recDate.Date }, cancellationToken: ct));
    }

    public async Task SaveRecommendationsAsync(IEnumerable<RecommendationRecord> recs, CancellationToken ct = default)
    {
        var list = recs.ToList();
        if (list.Count == 0) return;

        await using var conn = await _factory.OpenAsync(ct);
        foreach (var r in list)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                @"MERGE dbo.Recommendation AS t
                  USING (SELECT @b AS Board, @rd AS RecDate, @no AS MatchNo) AS s
                  ON t.Board = s.Board AND t.RecDate = s.RecDate AND t.MatchNo = s.MatchNo
                  WHEN MATCHED THEN UPDATE SET
                    Expect=@e, MatchId=@mid, HomeTeam=@h, AwayTeam=@a, MatchTime=@mt,
                    RecResult=@r, Confidence=@c, SignalLevel=@lv, IsCoverAll=@cover, SignalDesc=@desc,
                    Score1=@s1, Score2=@s2, Goals1=@g1, Goals2=@g2, RecHandicap=@rh, RecHalfFull=@hf,
                    Handicap=@hcp,
                    ActualScore = CASE WHEN t.MatchId = @mid THEN t.ActualScore END,
                    ActualGoals = CASE WHEN t.MatchId = @mid THEN t.ActualGoals END,
                    ActualResult = CASE WHEN t.MatchId = @mid THEN t.ActualResult END,
                    Hit = CASE
                        WHEN @cover = 1 OR @r IS NULL OR t.ActualResult IS NULL OR t.MatchId <> @mid OR @mid IS NULL THEN NULL
                        WHEN t.ActualResult = @r THEN 1 ELSE 0 END,
                    HandicapHit = NULL
                  WHEN NOT MATCHED THEN INSERT
                    (Board, Expect, MatchId, MatchNo, HomeTeam, AwayTeam, MatchTime, RecResult, Confidence, SignalLevel, IsCoverAll, SignalDesc, Score1, Score2, Goals1, Goals2, RecHandicap, RecHalfFull, Handicap, RecDate)
                    VALUES (@b, @e, @mid, @no, @h, @a, @mt, @r, @c, @lv, @cover, @desc, @s1, @s2, @g1, @g2, @rh, @hf, @hcp, @rd);",
                new
                {
                    b = r.Board, e = r.Expect, mid = r.MatchId, no = r.MatchNo,
                    h = r.HomeTeam, a = r.AwayTeam, mt = r.MatchTime, r = r.RecResult,
                    c = r.Confidence, lv = r.SignalLevel, cover = r.IsCoverAll, desc = r.SignalDesc,
                    s1 = r.Score1, s2 = r.Score2, g1 = r.Goals1, g2 = r.Goals2,
                    rh = r.RecHandicap, hf = r.RecHalfFull, hcp = r.Handicap, rd = r.RecDate
                }, cancellationToken: ct));
        }
    }

    /// <summary>按 fid 批量取比赛时间（胜负彩等板块 MatchTime 缺失时补时间用）。</summary>
    public async Task<Dictionary<long, DateTime>> GetMatchTimesAsync(IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long MatchId, DateTime MatchTime)>(new CommandDefinition(
            @"SELECT MatchId, MatchTime FROM dbo.FbMatch WHERE MatchId IN @ids",
            new { ids }, cancellationToken: ct));
        return rows.ToDictionary(r => r.MatchId, r => r.MatchTime);
    }

    /// <summary>复盘用赛果。未完场也返回，好在文档里写明还没打完。</summary>
    public async Task<Dictionary<long, ReviewScore>> GetReviewScoresAsync(IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, ReviewScore>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<ReviewScore>(new CommandDefinition(
                @"SELECT MatchId, HomeScore, AwayScore, HalfHomeScore, HalfAwayScore, Status
                  FROM dbo.FbMatch
                  WHERE MatchId IN @ids",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var row in rows)
                result[row.MatchId] = row;
        }
        return result;
    }

    /// <summary>按 fid 批量取已完场比赛的赛果（回填用）</summary>
    public async Task<Dictionary<long, (int? Home, int? Away, char? ResultSpf)>> GetResultsAsync(IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, (int?, int?, char?)>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<MatchResult>(new CommandDefinition(
                @"SELECT MatchId, HomeScore, AwayScore, ResultSpf
                  FROM dbo.FbMatch
                  WHERE Status = 3 AND MatchId IN @ids",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var row in rows)
                result[row.MatchId] = (row.HomeScore, row.AwayScore, row.ResultSpf);
        }
        return result;
    }

    private static IEnumerable<long[]> Chunk(long[] ids, int size = 500)
    {
        for (var i = 0; i < ids.Length; i += size)
        {
            var n = Math.Min(size, ids.Length - i);
            var chunk = new long[n];
            Array.Copy(ids, i, chunk, 0, n);
            yield return chunk;
        }
    }

    private sealed class MatchResult
    {
        public long MatchId { get; set; }
        public int? HomeScore { get; set; }
        public int? AwayScore { get; set; }
        public char? ResultSpf { get; set; }
    }

    /// <summary>回填一条推荐的实际赛果与命中</summary>
    public async Task BackfillResultAsync(long recId, string actualScore, int actualGoals, char actualResult, bool? hit,
        bool? handicapHit = null, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.Recommendation
              SET ActualScore=@s, ActualGoals=@g, ActualResult=@r, Hit=@h, HandicapHit=@hh
              WHERE RecId=@id",
            new { id = recId, s = actualScore, g = actualGoals, r = actualResult, h = hit, hh = handicapHit }, cancellationToken: ct));
    }

    /// <summary>取某日已保存的推荐记录（含赛果，用于复盘）</summary>
    public async Task<List<RecommendationRecord>> GetRecommendationsByDateAsync(DateTime date, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<RecommendationRecord>(new CommandDefinition(
            @"SELECT * FROM dbo.Recommendation WHERE RecDate = @d ORDER BY Board, Confidence DESC",
            new { d = date.Date }, cancellationToken: ct))).ToList();
    }

    /// <summary>取尚未回填赛果、或回填的比分和完场比分对不上的推荐记录</summary>
    public async Task<List<RecommendationRecord>> GetUnsettledRecommendationsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<RecommendationRecord>(new CommandDefinition(
            @"SELECT r.* FROM dbo.Recommendation r
              LEFT JOIN dbo.FbMatch f ON f.MatchId = r.MatchId AND f.Status = 3
              WHERE r.MatchId IS NOT NULL
                AND (r.ActualResult IS NULL
                     OR (r.RecHandicap IS NOT NULL AND r.Handicap IS NOT NULL AND r.HandicapHit IS NULL)
                     OR (f.MatchId IS NOT NULL AND (
                         ISNULL(r.ActualScore, '') <> CONCAT(f.HomeScore, '-', f.AwayScore)
                         OR r.ActualResult <> f.ResultSpf)))",
            cancellationToken: ct))).ToList();
    }

    /// <summary>历史推荐列表（按场次去重，每场取最新一条，按比赛时间降序，支持分页）</summary>
    public async Task<List<RecommendationRecord>> GetRecommendationListAsync(int take = 500, int offset = 0, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<RecommendationRecord>(new CommandDefinition(
            @"WITH cte AS (
                 SELECT *, ROW_NUMBER() OVER (PARTITION BY MatchId, CASE WHEN MatchId IS NULL THEN RecId ELSE 0 END ORDER BY CreatedAt DESC, RecId DESC) AS rn
                 FROM dbo.Recommendation
               )
               SELECT * FROM cte WHERE rn = 1
               ORDER BY MatchTime DESC, RecId DESC
               OFFSET @o ROWS FETCH NEXT @t ROWS ONLY",
            new { o = offset, t = take }, cancellationToken: ct))).ToList();
    }

    /// <summary>复盘列表（已回填赛果，按场次去重取最新一条，按比赛时间降序，JOIN FbMatch 取联赛名，支持分页）</summary>
    public async Task<List<RecommendationRecord>> GetReviewedListAsync(int take = 500, int offset = 0, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<RecommendationRecord>(new CommandDefinition(
            @"WITH cte AS (
                 SELECT r.*, m.LeagueName,
                        ROW_NUMBER() OVER (PARTITION BY r.MatchId, CASE WHEN r.MatchId IS NULL THEN r.RecId ELSE 0 END ORDER BY r.CreatedAt DESC, r.RecId DESC) AS rn
                 FROM dbo.Recommendation r
                 LEFT JOIN dbo.FbMatch m ON m.MatchId = r.MatchId
                 WHERE r.ActualResult IS NOT NULL
               )
               SELECT * FROM cte WHERE rn = 1
               ORDER BY MatchTime DESC, RecId DESC
               OFFSET @o ROWS FETCH NEXT @t ROWS ONLY",
            new { o = offset, t = take }, cancellationToken: ct))).ToList();
    }

    /// <summary>历史推荐总数（按场次去重）</summary>
    public async Task<int> CountRecommendationsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            @"SELECT COUNT(*) FROM (SELECT MatchId FROM dbo.Recommendation GROUP BY MatchId) t", cancellationToken: ct));
    }

    /// <summary>已回填赛果的推荐总数（按场次去重）</summary>
    public async Task<int> CountReviewedAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            @"SELECT COUNT(*) FROM (SELECT MatchId FROM dbo.Recommendation WHERE ActualResult IS NOT NULL GROUP BY MatchId) t", cancellationToken: ct));
    }

    /// <summary>复盘全量统计（按场次去重，取每场最新一条）：总数 + 命中数</summary>
    public async Task<(int Total, int Hit)> GetReviewedStatsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleAsync<dynamic>(new CommandDefinition(
            @"SELECT
                COUNT(*) AS Total,
                SUM(CASE WHEN Hit = 1 THEN 1 ELSE 0 END) AS Hit
              FROM (
                SELECT Hit,
                       ROW_NUMBER() OVER (PARTITION BY MatchId, CASE WHEN MatchId IS NULL THEN RecId ELSE 0 END ORDER BY CreatedAt DESC, RecId DESC) AS rn
                FROM dbo.Recommendation
                WHERE ActualResult IS NOT NULL
                  AND ISNULL(IsCoverAll, 0) = 0
                  AND RecResult IN ('3', '1', '0')
                  AND Confidence >= 60
              ) t WHERE rn = 1", cancellationToken: ct));
        return ((int)row.Total, (int?)row.Hit ?? 0);
    }

    /// <summary>
    /// 按场次号（如「周三001」）反查球队名与比赛时间，用于 OCR 录入购买记录时自动补全对阵。
    /// 返回 map：matchNo → (HomeTeam, AwayTeam, MatchTime, LeagueName)。
    /// </summary>
    public async Task<Dictionary<string, MatchRef>> ResolveMatchesByNoAsync(IEnumerable<string> matchNos, CancellationToken ct = default)
    {
        var nos = matchNos.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct().ToArray();
        if (nos.Length == 0) return new Dictionary<string, MatchRef>();

        await using var conn = await _factory.OpenAsync(ct);
        // 优先受注场次（BettingCard），再退回推荐表；OCR 场次经常不在精选里
        var rows = (await conn.QueryAsync<MatchRef>(new CommandDefinition(
            @"SELECT MatchNo, HomeTeam, AwayTeam, MatchTime, LeagueName, rn FROM (
                SELECT c.MatchNo, c.HomeTeam, c.AwayTeam, c.MatchTime, c.LeagueName,
                       ROW_NUMBER() OVER (PARTITION BY c.MatchNo ORDER BY c.CapturedAt DESC) AS rn, 1 AS pri
                FROM dbo.BettingCard c
                WHERE c.MatchNo IN @nos AND c.HomeTeam IS NOT NULL
                UNION ALL
                SELECT r.MatchNo, r.HomeTeam, r.AwayTeam, r.MatchTime, m.LeagueName,
                       ROW_NUMBER() OVER (PARTITION BY r.MatchNo ORDER BY r.RecId DESC) AS rn, 2 AS pri
                FROM dbo.Recommendation r
                LEFT JOIN dbo.FbMatch m ON m.MatchId = r.MatchId
                WHERE r.MatchNo IN @nos AND r.HomeTeam IS NOT NULL
              ) x
              WHERE rn = 1
              ORDER BY pri",
            new { nos }, cancellationToken: ct))).ToList();

        return rows
            .GroupBy(r => r.MatchNo)
            .ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>录入时按场次号带出主客队。先对上板块，对不上再取最近一张受注卡。</summary>
    public async Task<MatchRef?> FindCardByNoAsync(string matchNo, string? board, string? expect, CancellationToken ct = default)
    {
        var no = matchNo.Trim();
        if (no.Length == 0) return null;
        board = string.IsNullOrWhiteSpace(board) ? null : board.Trim();
        expect = string.IsNullOrWhiteSpace(expect) ? null : expect.Trim();

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<MatchRef>(new CommandDefinition(
            @"SELECT TOP 1 MatchNo, HomeTeam, AwayTeam, MatchTime, LeagueName
              FROM dbo.BettingCard
              WHERE MatchNo = @no AND HomeTeam IS NOT NULL
                AND (@board IS NULL OR Board = @board)
                AND (@expect IS NULL OR Expect = @expect)
              ORDER BY CapturedAt DESC",
            new { no, board, expect }, cancellationToken: ct));
    }

    /// <summary>
    /// OCR 队名碎片模糊匹配受注场次。用于场次号被吃掉时，靠「弗洛西诺」「哈马比」找回周四003/004。
    /// </summary>
    public async Task<MatchRef?> ResolveMatchByTeamHintAsync(
        string? weekPrefix, string? homeHint, string? awayHint, CancellationToken ct = default)
    {
        homeHint = CleanTeamHint(homeHint);
        awayHint = CleanTeamHint(awayHint);
        if (string.IsNullOrEmpty(homeHint) && string.IsNullOrEmpty(awayHint)) return null;

        await using var conn = await _factory.OpenAsync(ct);
        var cards = (await conn.QueryAsync<MatchRef>(new CommandDefinition(
            @"SELECT MatchNo, HomeTeam, AwayTeam, MatchTime, LeagueName
              FROM (
                SELECT c.MatchNo, c.HomeTeam, c.AwayTeam, c.MatchTime, c.LeagueName,
                       ROW_NUMBER() OVER (PARTITION BY c.MatchNo ORDER BY c.CapturedAt DESC) AS rn
                FROM dbo.BettingCard c
                WHERE c.HomeTeam IS NOT NULL
                  AND (@week IS NULL OR c.MatchNo LIKE @week + N'%')
              ) x WHERE rn = 1",
            new { week = string.IsNullOrEmpty(weekPrefix) ? null : weekPrefix },
            cancellationToken: ct))).ToList();

        MatchRef? best = null;
        var bestScore = 0;
        foreach (var c in cards)
        {
            var score = 0;
            if (!string.IsNullOrEmpty(homeHint))
            {
                if (ContainsTeam(c.HomeTeam, homeHint) || ContainsTeam(homeHint, c.HomeTeam)) score += 3;
                else if (ContainsTeam(c.AwayTeam, homeHint) || ContainsTeam(homeHint, c.AwayTeam)) score += 2;
            }
            if (!string.IsNullOrEmpty(awayHint))
            {
                if (ContainsTeam(c.AwayTeam, awayHint) || ContainsTeam(awayHint, c.AwayTeam)) score += 3;
                else if (ContainsTeam(c.HomeTeam, awayHint) || ContainsTeam(awayHint, c.HomeTeam)) score += 2;
            }
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        return bestScore >= 2 ? best : null;
    }

    private static string? CleanTeamHint(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.Length < 2) return null;
        // OCR 噪点
        if (s is "全场比分" or "投注内容" or "竞彩足球") return null;
        return s;
    }

    private static bool ContainsTeam(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase)) return true;
        if (b.Length >= 2 && a.Contains(b[..Math.Min(2, b.Length)], StringComparison.OrdinalIgnoreCase)) return true;
        // 两边取较长公共子串 ≥ 2
        for (var len = Math.Min(a.Length, b.Length); len >= 2; len--)
        {
            for (var i = 0; i + len <= b.Length; i++)
            {
                if (a.Contains(b.Substring(i, len), StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
}

/// <summary>场次号 → 对阵信息</summary>
public sealed class MatchRef
{
    public string MatchNo { get; set; } = "";
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }
    public string? LeagueName { get; set; }
    public int rn { get; set; }
}
