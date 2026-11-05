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

    /// <summary>
    /// 大奶回测用的历史受注场次：当天开赛、或当天受注过的场次。
    ///
    /// 不复用 <see cref="GetCardsByKickoffAsync"/>（2026-11-05 实测踩到）：BettingCard 按
    /// (Board, Expect, MatchNo) 做 MERGE，而「周X00N」每周都会重号，旧一天的卡片会被新一天的覆盖。
    /// 拿它回测 11-04，六场欧冠只剩一场，另外五场连影子都没有。赔率历史表反而是全的。
    ///
    /// 口径为什么不是纯「开赛日」：欧冠是北京时间凌晨开赛，受注和出票都在前一天。
    /// 11-04 那三场反掉的比赛开赛时间是 11-05 04:00，只按开赛日筛会把它们全漏掉——
    /// 而它们正是这道闸要复盘的对象。所以受注日也要算进来。
    /// </summary>
    public async Task<List<BettingCardRecord>> GetMilkBacktestCardsAsync(
        DateTime day, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<BettingCardRecord>(new CommandDefinition(
            @"WITH ids AS (
                  SELECT j.MatchId, MAX(j.MatchNo) AS MatchNo
                  FROM dbo.FbJcOddsHistory j
                  WHERE j.MatchId IS NOT NULL AND j.PlayType = 'SPF' AND j.Odds > 1
                  GROUP BY j.MatchId
              )
              SELECT i.MatchId, i.MatchNo, m.LeagueName,
                     m.HomeTeamName AS HomeTeam, m.AwayTeamName AS AwayTeam, m.MatchTime
              FROM ids i
              JOIN dbo.FbMatch m ON m.MatchId = i.MatchId
              WHERE CONVERT(date, m.MatchTime) = @d
                 OR EXISTS (SELECT 1 FROM dbo.FbMilkPick p
                            WHERE p.MatchId = m.MatchId AND p.PickDate = @d)
              ORDER BY m.MatchTime",
            new { d = day.Date }, cancellationToken: ct))).ToList();
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

    /// <summary>
    /// 采样新鲜度过滤。盘口表都是「一张表存多次采集」，查询只取最新一行并不能保证它够新：
    /// 一场三天没采到的比赛，最新一行就是三天前的，拿它算「升/降」等于把旧盘当现盘。
    /// 传 null 或 0 表示不加限制（复盘、历史回看要用全部数据）。
    /// </summary>
    private static string FreshFilter(double? hours, string column = "CapturedAt")
        => hours is > 0 ? $" AND {column} >= DATEADD(MINUTE, -{(int)(hours.Value * 60)}, SYSDATETIME())" : "";

    /// <summary>按 fid 批量取欧赔主胜/平局 初盘→终盘变化（平均值 CompanyId=0）</summary>
    public async Task<Dictionary<long, (decimal? Home, decimal? Draw)>> GetEuOddsDeltaAsync(
        IEnumerable<long> matchIds, CancellationToken ct = default, double? freshHours = null)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, (decimal?, decimal?)>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<EuDeltaRow>(new CommandDefinition(
                $@"SELECT MatchId,
                         MAX(CASE WHEN IsOpening = 0 THEN HomeWin END)
                       - MAX(CASE WHEN IsOpening = 1 THEN HomeWin END) AS HomeDelta,
                         MAX(CASE WHEN IsOpening = 0 THEN Draw END)
                       - MAX(CASE WHEN IsOpening = 1 THEN Draw END) AS DrawDelta
                  FROM dbo.FbOdds
                  WHERE MatchId IN @ids AND OddsType = 'EU' AND CompanyId = 0{FreshFilter(freshHours)}
                  GROUP BY MatchId",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var row in rows)
                result[row.MatchId] = (row.HomeDelta, row.DrawDelta);
        }
        return result;
    }

    /// <summary>按 fid 批量取欧赔初盘主/平/客（平均值 CompanyId=0，IsOpening=1）。</summary>
    public async Task<Dictionary<long, (decimal? Home, decimal? Draw, decimal? Away)>> GetOpenEuAsync(
        IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, (decimal?, decimal?, decimal?)>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<ClosingEuRow>(new CommandDefinition(
                $@"SELECT MatchId,
                         MAX(CASE WHEN IsOpening = 1 THEN HomeWin END) AS HomeWin,
                         MAX(CASE WHEN IsOpening = 1 THEN Draw END) AS Draw,
                         MAX(CASE WHEN IsOpening = 1 THEN AwayWin END) AS AwayWin
                  FROM dbo.FbOdds
                  WHERE MatchId IN @ids AND OddsType = 'EU' AND CompanyId = 0
                  GROUP BY MatchId",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var row in rows)
                result[row.MatchId] = (row.HomeWin, row.Draw, row.AwayWin);
        }
        return result;
    }

    /// <summary>按 fid 批量取欧赔终盘主/平/客（平均值 CompanyId=0），受注场次没有 SP 时用来判断冷热。</summary>
    public async Task<Dictionary<long, (decimal? Home, decimal? Draw, decimal? Away)>> GetClosingEuAsync(
        IEnumerable<long> matchIds, CancellationToken ct = default, double? freshHours = null)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, (decimal?, decimal?, decimal?)>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<ClosingEuRow>(new CommandDefinition(
                $@"SELECT MatchId,
                         MAX(CASE WHEN IsOpening = 0 THEN HomeWin END) AS HomeWin,
                         MAX(CASE WHEN IsOpening = 0 THEN Draw END) AS Draw,
                         MAX(CASE WHEN IsOpening = 0 THEN AwayWin END) AS AwayWin
                  FROM dbo.FbOdds
                  WHERE MatchId IN @ids AND OddsType = 'EU' AND CompanyId = 0{FreshFilter(freshHours)}
                  GROUP BY MatchId",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var row in rows)
                result[row.MatchId] = (row.HomeWin, row.Draw, row.AwayWin);
        }
        return result;
    }

    /// <summary>
    /// 按 fid 取该场盘口（欧赔 + 必发）的最新采集时间。写进推荐记录后，
    /// 事后能判断「这条推荐当时用的是不是新鲜盘」，不需要猜。
    /// </summary>
    public async Task<Dictionary<long, DateTime>> GetMarketFreshnessAsync(
        IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, DateTime>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<(long MatchId, DateTime At)>(new CommandDefinition(
                @"SELECT MatchId, MAX(CapturedAt) AS At
                  FROM (
                      SELECT MatchId, MAX(CapturedAt) AS CapturedAt FROM dbo.FbOdds WHERE MatchId IN @ids GROUP BY MatchId
                      UNION ALL
                      SELECT MatchId, MAX(CapturedAt) AS CapturedAt FROM dbo.FbExchange WHERE MatchId IN @ids GROUP BY MatchId
                  ) t
                  GROUP BY MatchId",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var row in rows)
                result[row.MatchId] = row.At;
        }
        return result;
    }

    /// <summary>亚盘初盘和终盘。终盘比初盘更小表示更看好主队。</summary>
    public async Task<Dictionary<long, (decimal? Open, decimal? Close)>> GetAsianMoveAsync(
        IEnumerable<long> matchIds, CancellationToken ct = default, double? freshHours = null)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, (decimal?, decimal?)>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<AsianMoveRow>(new CommandDefinition(
                $@"SELECT MatchId,
                         MAX(CASE WHEN IsOpening = 1 THEN Handicap END) AS OpenHcp,
                         MAX(CASE WHEN IsOpening = 0 THEN Handicap END) AS CloseHcp
                  FROM dbo.FbOdds
                  WHERE MatchId IN @ids AND OddsType = 'AH' AND CompanyId = 0{FreshFilter(freshHours)}
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

    /// <summary>
    /// 近 18 个月完场比分，供球队攻防估计。
    ///
    /// <paramref name="before"/> 是回测用的时间闸：只取该时刻之前开赛的样本。
    /// 不传就是全量——线上正常用。回测历史必须传，否则模型已经见过当天的赛果，
    /// 「模型当时怎么想」这个问题就永远答不出来（2026-11-05 实测：按全量重训去回测 11-04 的朗斯，
    /// 模型竟然给出「客胜」，因为它已经知道科莫打了 9 个球）。
    /// </summary>
    public async Task<List<ScoreSample>> GetScoreSamplesAsync(
        CancellationToken ct = default, DateTime? before = null)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ScoreSample>(new CommandDefinition(
            @"SELECT f.LeagueName, f.HomeTeamName AS HomeTeam, f.AwayTeamName AS AwayTeam,
                     f.HomeScore, f.AwayScore
              FROM dbo.FbMatch f
              WHERE f.Status = 3
                AND f.HomeScore IS NOT NULL AND f.AwayScore IS NOT NULL
                AND f.MatchTime >= DATEADD(month, -18, GETDATE())
                AND (@before IS NULL OR f.MatchTime < @before)",
            new { before }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// 尾盘主胜变化：最近采样 − 至少 4 小时前的采样（无则用最早点）。
    /// 正值=尾盘抬高主胜赔（看淡主）；负值=尾盘继续压主。
    /// </summary>
    public async Task<Dictionary<long, decimal?>> GetLateHomeSteamAsync(
        IEnumerable<long> matchIds, CancellationToken ct = default, double? freshHours = null)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, decimal?>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<(long MatchId, decimal? LateDelta)>(new CommandDefinition(
                $@"WITH pts AS (
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
                  INNER JOIN earlier e ON e.MatchId = l.MatchId AND e.rn = 1
                  WHERE 1 = 1{FreshFilter(freshHours, "l.CapturedAt")}",
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
            // 2026-11-05 性能修复：原实现是相关子查询——外层每场对 FbMatch（40 万行）扫两次
            // （HomeTeamId OR AwayTeamId，OR 走不了索引），数据回填后从毫秒退化到 13 秒，
            // 拖死整个 /api/recommend。改成窗口函数：一次扫 FbMatch，按球队取最近 5 场求分，
            // 物化成 form 再 JOIN，O(27×40万) 降到 O(40万)。
            //
            // 口径差异：原来「截至 m.MatchTime 之前的最近 5 场」，现在「截至当前最近 5 场」。
            // 实时推荐里 m.MatchTime 都在未来，两者完全等价；复盘里 m 是历史场次会有轻微偏差，
            // 但这个字段只喂方向推荐（已 2026-11-04 停发、仅落库校准台账），不影响对外发布的大奶榜。
            var rows = await conn.QueryAsync<(long MatchId, int? HomePts, int? AwayPts)>(new CommandDefinition(
                @"WITH ranked AS (
                      SELECT TeamId, MatchTime, Pts,
                             ROW_NUMBER() OVER (PARTITION BY TeamId ORDER BY MatchTime DESC) AS rn
                      FROM (
                          SELECT HomeTeamId AS TeamId, MatchTime,
                                 CASE WHEN ResultSpf = '3' THEN 3
                                      WHEN ResultSpf = '1' THEN 1 ELSE 0 END AS Pts
                          FROM dbo.FbMatch
                          WHERE Status = 3 AND ResultSpf IN ('3','1','0') AND HomeTeamId IS NOT NULL
                          UNION ALL
                          SELECT AwayTeamId AS TeamId, MatchTime,
                                 CASE WHEN ResultSpf = '0' THEN 3
                                      WHEN ResultSpf = '1' THEN 1 ELSE 0 END AS Pts
                          FROM dbo.FbMatch
                          WHERE Status = 3 AND ResultSpf IN ('3','1','0') AND AwayTeamId IS NOT NULL
                      ) u
                  ),
                  form AS (
                      SELECT TeamId, SUM(Pts) AS Pts5
                      FROM ranked WHERE rn <= 5
                      GROUP BY TeamId
                  )
                  SELECT m.MatchId, fh.Pts5 AS HomePts, fa.Pts5 AS AwayPts
                  FROM dbo.FbMatch m
                  LEFT JOIN form fh ON fh.TeamId = m.HomeTeamId
                  LEFT JOIN form fa ON fa.TeamId = m.AwayTeamId
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

    /// <summary>
    /// 按 fid 批量取必发主/平/客投注占比（最新快照）。
    ///
    /// <paramref name="asOfKickoff"/> 只给复盘用：必发会一直记到赛中甚至赛后
    /// （1722 场完场比赛里 1697 条采集晚于开赛），拿最后一条去重算等于把赛中资金
    /// ——也就是比分已经发生之后的信息——喂进信号。复盘要还原「当时会怎么判」，
    /// 必须卡在开赛时刻。
    /// </summary>
    public async Task<Dictionary<long, (decimal? Home, decimal? Draw, decimal? Away)>> GetExchangeRatioAsync(
        IEnumerable<long> matchIds, CancellationToken ct = default, double? freshHours = null,
        bool asOfKickoff = false)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, (decimal?, decimal?, decimal?)>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<ExchangeRatio>(new CommandDefinition(
                $@"SELECT MatchId, RatioHome, RatioDraw, RatioAway
                  FROM (
                      SELECT MatchId, RatioHome, RatioDraw, RatioAway,
                             ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt DESC) AS rn
                      FROM {(asOfKickoff
                          ? @"(SELECT e.* FROM dbo.FbExchange e
                               JOIN dbo.FbMatch m ON m.MatchId = e.MatchId
                              WHERE e.MatchId IN @ids AND e.CapturedAt <= m.MatchTime)"
                          : "dbo.FbExchange")} x
                      WHERE {(asOfKickoff ? "1 = 1" : $"MatchId IN @ids{FreshFilter(freshHours)}")}
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
        // 只清还没结算的。已回填赛果的行删了就永久丢失命中记录，
        // 而重算本身会越来越频繁（T−60min 那一轮每 10 分钟一次），
        // 一天里晚场重算会把早场的赛果反复抹掉。
        await conn.ExecuteAsync(new CommandDefinition(
            @"DELETE FROM dbo.Recommendation
              WHERE Board = @b AND RecDate = @rd AND ActualResult IS NULL",
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
                    Handicap=@hcp, HitProbability=@hp, HitProbNote=@hpn, PickOdds=@po, MarketProb=@mp, ModelTag=@mtag, DataAt=@dat,
                    ActualScore = CASE WHEN t.MatchId = @mid THEN t.ActualScore END,
                    ActualGoals = CASE WHEN t.MatchId = @mid THEN t.ActualGoals END,
                    ActualResult = CASE WHEN t.MatchId = @mid THEN t.ActualResult END,
                    Hit = CASE
                        WHEN @cover = 1 OR @r IS NULL OR t.ActualResult IS NULL OR t.MatchId <> @mid OR @mid IS NULL THEN NULL
                        WHEN t.ActualResult = @r THEN 1 ELSE 0 END,
                    HandicapHit = NULL
                  WHEN NOT MATCHED THEN INSERT
                    (Board, Expect, MatchId, MatchNo, HomeTeam, AwayTeam, MatchTime, RecResult, Confidence, SignalLevel, IsCoverAll, SignalDesc, Score1, Score2, Goals1, Goals2, RecHandicap, RecHalfFull, Handicap, RecDate, HitProbability, HitProbNote, PickOdds, MarketProb, ModelTag, DataAt)
                    VALUES (@b, @e, @mid, @no, @h, @a, @mt, @r, @c, @lv, @cover, @desc, @s1, @s2, @g1, @g2, @rh, @hf, @hcp, @rd, @hp, @hpn, @po, @mp, @mtag, @dat);",
                new
                {
                    b = r.Board, e = r.Expect, mid = r.MatchId, no = r.MatchNo,
                    h = r.HomeTeam, a = r.AwayTeam, mt = r.MatchTime, r = r.RecResult,
                    c = r.Confidence, lv = r.SignalLevel, cover = r.IsCoverAll, desc = r.SignalDesc,
                    s1 = r.Score1, s2 = r.Score2, g1 = r.Goals1, g2 = r.Goals2,
                    rh = r.RecHandicap, hf = r.RecHalfFull, hcp = r.Handicap, rd = r.RecDate,
                    hp = r.HitProbability, hpn = r.HitProbNote, po = r.PickOdds, mp = r.MarketProb,
                    mtag = r.ModelTag, dat = r.DataAt
                }, cancellationToken: ct));
        }
    }

    /* ---------------- 否决台账 ---------------- */

    /// <summary>
    /// 写入当天被规则挡下的场次。同一板块同一天按场次号覆盖，
    /// 已回填的赛果只在 MatchId 未变时保留——重跑作业不该把结算结果弄丢。
    /// </summary>
    public async Task SaveVetoesAsync(IEnumerable<VetoRecord> rows, CancellationToken ct = default)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;

        await using var conn = await _factory.OpenAsync(ct);
        foreach (var r in list)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                @"MERGE dbo.RecommendationVeto AS t
                  USING (SELECT @b AS Board, @rd AS RecDate, @no AS MatchNo) AS s
                  ON t.Board = s.Board AND t.RecDate = s.RecDate AND t.MatchNo = s.MatchNo
                  WHEN MATCHED THEN UPDATE SET
                    MatchId=@mid, HomeTeam=@h, AwayTeam=@a, MatchTime=@mt,
                    Pick=@pick, HcpPick=@hcpPick, Handicap=@hcp, ReasonCode=@code, Reason=@reason,
                    SignalLevel=@lv, HitProbability=@hp, HcpProbability=@hhp, PickOdds=@po,
                    MarketProb=@mp, RatioHome=@rh, RatioAway=@ra, OddsDelta=@od,
                    ActualResult = CASE WHEN t.MatchId = @mid THEN t.ActualResult END,
                    ActualScore  = CASE WHEN t.MatchId = @mid THEN t.ActualScore END,
                    Hit          = CASE WHEN t.MatchId = @mid THEN t.Hit END,
                    HcpHit       = CASE WHEN t.MatchId = @mid THEN t.HcpHit END
                  WHEN NOT MATCHED THEN INSERT
                    (Board, RecDate, MatchId, MatchNo, HomeTeam, AwayTeam, MatchTime, Pick, HcpPick,
                     Handicap, ReasonCode, Reason, SignalLevel, HitProbability, HcpProbability,
                     PickOdds, MarketProb, RatioHome, RatioAway, OddsDelta)
                    VALUES (@b, @rd, @mid, @no, @h, @a, @mt, @pick, @hcpPick, @hcp, @code, @reason,
                            @lv, @hp, @hhp, @po, @mp, @rh, @ra, @od);",
                new
                {
                    b = r.Board, rd = r.RecDate.Date, mid = r.MatchId, no = r.MatchNo,
                    h = r.HomeTeam, a = r.AwayTeam, mt = r.MatchTime,
                    pick = r.Pick, hcpPick = r.HcpPick, hcp = r.Handicap,
                    code = r.ReasonCode, reason = r.Reason, lv = r.SignalLevel,
                    hp = r.HitProbability, hhp = r.HcpProbability, po = r.PickOdds,
                    mp = r.MarketProb, rh = r.RatioHome, ra = r.RatioAway, od = r.OddsDelta
                }, cancellationToken: ct));
        }
    }

    /// <summary>删除某板块某天的否决记录（重跑当天作业时先清，避免残留旧场次）。</summary>
    public async Task DeleteVetoesByBoardDateAsync(string board, DateTime recDate, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            @"DELETE FROM dbo.RecommendationVeto WHERE Board = @b AND RecDate = @rd",
            new { b = board, rd = recDate.Date }, cancellationToken: ct));
    }

    /// <summary>取还没判定的否决记录。</summary>
    public async Task<List<VetoRecord>> GetUnsettledVetoesAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<VetoRecord>(new CommandDefinition(
            // 只回看近 7 天：更早还没判定的场次基本是永远不会有赛果的，
            // 留在结果集里每轮都要白扫一遍。
            // 取消场次（Status=2）永远等不到赛果，直接排除出待判队列（Hit 保持 NULL、不进统计分母）。
            @"SELECT * FROM dbo.RecommendationVeto
              WHERE Hit IS NULL AND MatchId IS NOT NULL
                AND RecDate >= DATEADD(DAY, -7, CAST(GETDATE() AS DATE))
                AND NOT EXISTS (SELECT 1 FROM dbo.FbMatch x
                                WHERE x.MatchId = RecommendationVeto.MatchId AND x.Status = 2)",
            cancellationToken: ct))).ToList();
    }

    /// <summary>回填否决记录的实际结果。</summary>
    public async Task BackfillVetoResultAsync(long vetoId, string actualScore, char actualResult,
        bool? hit, bool? hcpHit, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.RecommendationVeto
              SET ActualScore=@s, ActualResult=@r, Hit=@h, HcpHit=@hh
              WHERE VetoId=@id",
            new { id = vetoId, s = actualScore, r = actualResult, h = hit, hh = hcpHit },
            cancellationToken: ct));
    }

    /// <summary>按否决原因聚合：挡了多少场、照打会中多少、回报如何。</summary>
    public async Task<List<VetoStatRow>> GetVetoStatsAsync(int days, CancellationToken ct = default, string? board = null)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<VetoStatRow>(new CommandDefinition(
            @"SELECT ReasonCode,
                     COUNT(*) AS Total,
                     SUM(CASE WHEN Hit IS NOT NULL THEN 1 ELSE 0 END) AS Settled,
                     SUM(CASE WHEN Hit = 1 THEN 1 ELSE 0 END) AS Hits,
                     CAST(100.0 * SUM(CASE WHEN Hit = 1 THEN 1 ELSE 0 END)
                          / NULLIF(SUM(CASE WHEN Hit IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,1)) AS HitRate,
                     CAST(AVG(CASE WHEN Hit IS NOT NULL THEN PickOdds END) AS DECIMAL(8,2)) AS AvgOdds,
                     CAST(SUM(CASE WHEN Hit = 1 THEN PickOdds ELSE 0 END)
                          / NULLIF(SUM(CASE WHEN Hit IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(6,3)) AS Roi
              FROM dbo.RecommendationVeto
              WHERE RecDate >= @from AND (@board IS NULL OR Board = @board)
              GROUP BY ReasonCode
              ORDER BY COUNT(*) DESC",
            new { from = DateTime.Today.AddDays(-Math.Abs(days)), board }, cancellationToken: ct))).ToList();
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

    /// <summary>
    /// 校准用样本：已出赛果、有预测值、有方向的推荐行。全包与无方向的场次不计入——
    /// 它们本来就不参与命中率，放进来只会稀释结论。
    /// </summary>
    public async Task<List<CalibrationRow>> GetCalibrationRowsAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<CalibrationRow>(new CommandDefinition(
            @"SELECT RecId, Board, RecDate, MatchTime, HitProbability, MarketProb, PickOdds,
                     CAST(CASE WHEN Hit = 1 THEN 1 ELSE 0 END AS BIT) AS Hit,
                     ModelTag, DataAt, SignalLevel, RecResult, ActualResult
              FROM dbo.Recommendation
              WHERE RecDate >= @from AND RecDate <= @to
                AND HitProbability IS NOT NULL
                AND Hit IS NOT NULL
                AND RecResult IS NOT NULL",
            new { from = from.Date, to = to.Date }, cancellationToken: ct));
        return rows.ToList();
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

    /// <summary>
    /// 统计「马上要开赛、但上次算推荐用的盘还不算临场盘」的场次数量。
    ///
    /// 用途是 T−60min 重算：现在只有 08:40 / 11:40 / 16:40 / 20:40 四个保存时点，
    /// 16:40 存下的票到 20:00 开赛前发生的变化不会重算，于是「终盘规则」实际是在拿早盘价解释。
    /// 越临近开赛的盘越值钱，所以这里找出那些最后一版数据早于「开赛前 60 分钟」的场次。
    ///
    /// DataAt 是那次预测所依据的盘口采集时间，重算后会被刷成当前时刻，
    /// 因此同一场不会反复触发。
    /// </summary>
    public async Task<int> CountNearKickoffStaleAsync(
        int fromMinutes = 55, int toMinutes = 85, int freshMinutes = 60, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            @"SELECT COUNT(*)
              FROM dbo.Recommendation r
              WHERE r.MatchTime IS NOT NULL
                AND r.ActualResult IS NULL
                AND r.MatchTime BETWEEN DATEADD(minute, @from, SYSDATETIME())
                                    AND DATEADD(minute, @to, SYSDATETIME())
                AND (r.DataAt IS NULL OR r.DataAt < DATEADD(minute, -@fresh, r.MatchTime))",
            new { from = fromMinutes, to = toMinutes, fresh = freshMinutes }, cancellationToken: ct));
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
                AND NOT EXISTS (SELECT 1 FROM dbo.FbMatch x
                                WHERE x.MatchId = r.MatchId AND x.Status = 2) -- 取消场永远无赛果，不进待结队列
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
