using AiFootBall.Core.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AiFootBall.Data;

/// <summary>竞彩固定奖金（四类玩法）历史采样仓储。</summary>
public sealed class JcOddsRepository
{
    private readonly SqlConnectionFactory _factory;

    public JcOddsRepository(SqlConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// 变盘判定阈值：|(新价 − 旧价) / 旧价| ≥ 5% 才记一笔。
    ///
    /// 低赔率档的 5% 绝对量更小（3.00 → 3.15），这是想要的：热门方向动 5% 比冷门动 5% 有信息量。
    /// 阈值不能太小——采样是分钟级的，正常抖动会把它淹成噪声；也不能太大，11-04 朗斯
    /// 那次胜胜 5.85→6.20 只有 6.0%，阈值放到 8% 就整条漏掉了。
    /// </summary>
    public const decimal MoveThreshold = 0.05m;

    /// <summary>
    /// 把一批竞彩固定奖金快照写成时间序列点（同一 CapturedAt，先删旧再插，保证幂等）。
    ///
    /// watchMoves：采样时开（默认），回补历史时关。
    /// 回补是把「已经完结的终价」一次性插进去，跟当前库里的最新价没有时间先后关系，
    /// 拿它比对只会产出一堆假变盘。
    ///
    /// 同一事务里还做两件事，三件必须一起成功或一起失败：
    ///   1. 写 FbOddsScanLog —— 这一轮采了几场、几个选项、几个变盘（没有这条就分不清「漏采」和「没变化」）
    ///   2. 刷 FbMatchTrend  —— 本次涉及场次的「首轮→现价」走向汇总（页面直接读，不再现算 124 万行）
    /// </summary>
    public async Task<int> SaveSnapshotAsync(IReadOnlyList<JcOddsSnapshot> snapshots, DateTime capturedAt,
        bool watchMoves = true, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var points = new List<JcOddsHistoryPoint>();
        foreach (var s in snapshots)
        {
            foreach (var o in s.Options)
            {
                o.MatchId = s.MatchId;
                o.MatchNo = s.MatchNo;
                o.CapturedAt = capturedAt;
                points.Add(o);
            }
        }
        if (points.Count == 0) return 0;

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        // 先清掉本分钟已采样的旧数据，避免同分钟重复触发造成重复行
        var matchNos = points.Select(p => p.MatchNo).Distinct().ToArray();

        // 上一次采样的价格必须在 DELETE 之前取：本分钟的旧行马上要被删掉，
        // 删完再取就只能拿到更早一条，跨过一整轮采样。
        var prev = watchMoves
            ? await GetPrevSampledAsync(conn, tx, matchNos, capturedAt, ct)
            : new Dictionary<string, (decimal Odds, DateTime At)>(StringComparer.Ordinal);

        await conn.ExecuteAsync(new CommandDefinition(
            @"DELETE FROM dbo.FbJcOddsHistory WHERE CapturedAt = @at AND MatchNo IN @nos",
            new { at = capturedAt, nos = matchNos }, tx, cancellationToken: ct));

        var affected = 0;
        foreach (var p in points)
        {
            affected += await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbJcOddsHistory (MatchId, MatchNo, PlayType, OptionCode, OptionName, Odds, CapturedAt)
                  VALUES (@mid, @no, @pt, @code, @name, @odds, @at)",
                new
                {
                    mid = p.MatchId, no = p.MatchNo, pt = p.PlayType, code = p.OptionCode,
                    name = p.OptionName, odds = p.Odds, at = p.CapturedAt
                }, tx, cancellationToken: ct));
        }

        var moves = watchMoves
            ? await RecordMovesAsync(conn, tx, points, capturedAt, prev, ct)
            : 0;

        // 走向按「有 MatchId 的场次」刷新。MatchId 空的档写进去也归不到场，跳过。
        var matchIds = points.Where(p => p.MatchId is > 0).Select(p => p.MatchId!.Value).Distinct().ToArray();
        if (matchIds.Length > 0)
        {
            await RefreshTrendAsync(conn, tx, matchIds, ct);
            // 净胜球诊断和走向同事务刷新：两者读的是同一批快照，
            // 分开刷会出现「走向已是封盘价、净胜球还是上一轮」的错位。
            await RefreshScoreDiagAsync(conn, tx, matchIds, ct);
        }

        await LogScanAsync(conn, tx, "jc", capturedAt, snapshots.Count, affected, moves,
            (int)sw.ElapsedMilliseconds, ct);

        await tx.CommitAsync(ct);
        return affected;
    }

    /// <summary>
    /// 采集批次台账。一轮一行，唯一键 (Source, CapturedAt) —— 重跑同一分钟是覆盖，不是新增。
    ///
    /// 为什么值得单独一张表：11-05 上午 11:09 → 11:15 → 11:37，中间空了 22 分钟。
    /// 光看 FbJcOddsHistory 只知道「这 22 分钟价格没变」，不知道「根本没去采」。
    /// 调度停了、接口抽了、进程被杀，在明细表里全长得像「市场很平静」。
    /// </summary>
    private static async Task LogScanAsync(
        SqlConnection conn, SqlTransaction tx, string source, DateTime capturedAt,
        int matchCount, int optionCount, int moveCount, int elapsedMs, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            @"MERGE dbo.FbOddsScanLog AS t
              USING (SELECT @source AS Source, @at AS CapturedAt) AS s
                 ON t.Source = s.Source AND t.CapturedAt = s.CapturedAt
              WHEN MATCHED THEN UPDATE SET
                   MatchCount = @matches, OptionCount = @options, MoveCount = @moves,
                   ElapsedMs = @ms, Status = 'ok', LoggedAt = SYSDATETIME()
              WHEN NOT MATCHED THEN INSERT
                   (Source, CapturedAt, MatchCount, OptionCount, MoveCount, ElapsedMs, Status)
                   VALUES (@source, @at, @matches, @options, @moves, @ms, 'ok');",
            new
            {
                source, at = capturedAt, matches = matchCount, options = optionCount,
                moves = moveCount, ms = elapsedMs
            }, tx, cancellationToken: ct));
    }

    /// <summary>采样失败也要留痕。「今天没采到」和「今天没比赛」在页面上是两件事。</summary>
    public async Task LogScanFailureAsync(string source, DateTime capturedAt, string note, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            @"MERGE dbo.FbOddsScanLog AS t
              USING (SELECT @source AS Source, @at AS CapturedAt) AS s
                 ON t.Source = s.Source AND t.CapturedAt = s.CapturedAt
              WHEN MATCHED THEN UPDATE SET Status = 'error', Note = @note, LoggedAt = SYSDATETIME()
              WHEN NOT MATCHED THEN INSERT (Source, CapturedAt, MatchCount, OptionCount, MoveCount, Status, Note)
                   VALUES (@source, @at, 0, 0, 0, 'error', @note);",
            new { source, at = capturedAt, note }, cancellationToken: ct));
    }

    /// <summary>
    /// 重算指定场次的「每场 × 每玩法」走向。整场整玩法先删后插，不做增量累加——
    /// 累加要区分「新增一轮」和「重跑同一轮」，一旦算错就永远偏，重算便宜且永远对。
    ///
    /// 口径必须和 db/19_odds_capture.sql 的历史回填完全一致，否则老数据和新数据接不上。
    /// </summary>
    private static async Task RefreshTrendAsync(
        SqlConnection conn, SqlTransaction tx, long[] matchIds, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(TrendSql,
            new { ids = matchIds }, tx, cancellationToken: ct));
    }

    private const string TrendSql = @"
DELETE FROM dbo.FbMatchTrend WHERE MatchId IN @ids;

;WITH s AS (
    SELECT MatchId, PlayType, OptionCode, OptionName, Odds, CapturedAt,
           ROW_NUMBER() OVER (PARTITION BY MatchId, PlayType, OptionCode ORDER BY CapturedAt)      AS rf,
           ROW_NUMBER() OVER (PARTITION BY MatchId, PlayType, OptionCode ORDER BY CapturedAt DESC) AS rl
    FROM dbo.FbJcOddsHistory
    WHERE MatchId IN @ids AND Odds > 1
), f AS (SELECT * FROM s WHERE rf = 1),
   l AS (SELECT * FROM s WHERE rl = 1),
   agg AS (
    SELECT MatchId, PlayType, MIN(CapturedAt) AS FirstAt, MAX(CapturedAt) AS LastAt,
           COUNT(DISTINCT CapturedAt) AS Rounds, COUNT(DISTINCT OptionCode) AS Options
    FROM dbo.FbJcOddsHistory
    WHERE MatchId IN @ids AND Odds > 1
    GROUP BY MatchId, PlayType
   ),
   ft AS (SELECT MatchId, PlayType, OptionCode, OptionName, Odds FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY MatchId, PlayType ORDER BY Odds, OptionCode) rn
            FROM f) z WHERE rn = 1),
   lt AS (SELECT MatchId, PlayType, OptionCode, OptionName, Odds FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY MatchId, PlayType ORDER BY Odds, OptionCode) rn
            FROM l) z WHERE rn = 1),
   pf AS (SELECT MatchId, PlayType,
                 MAX(CASE WHEN OptionCode = '3' THEN Odds END) AS W,
                 MAX(CASE WHEN OptionCode = '1' THEN Odds END) AS D,
                 MAX(CASE WHEN OptionCode = '0' THEN Odds END) AS L
          FROM f WHERE PlayType IN ('SPF', 'NSPF') GROUP BY MatchId, PlayType),
   pl AS (SELECT MatchId, PlayType,
                 MAX(CASE WHEN OptionCode = '3' THEN Odds END) AS W,
                 MAX(CASE WHEN OptionCode = '1' THEN Odds END) AS D,
                 MAX(CASE WHEN OptionCode = '0' THEN Odds END) AS L
          FROM l WHERE PlayType IN ('SPF', 'NSPF') GROUP BY MatchId, PlayType),
   mv AS (SELECT MatchId, PlayType, COUNT(*) AS Moves,
                 SUM(CASE WHEN ABS(ChangePct) >= 0.10 THEN 1 ELSE 0 END) AS BigMoves,
                 SUM(CASE WHEN ChangePct > 0 THEN 1 ELSE 0 END) AS UpCount,
                 SUM(CASE WHEN ChangePct < 0 THEN 1 ELSE 0 END) AS DownCount,
                 MIN(MoveAt) AS FirstMoveAt, MAX(MoveAt) AS LastMoveAt,
                 SUM(CASE WHEN MinutesToKick IS NOT NULL AND MinutesToKick <= 180 THEN 1 ELSE 0 END) AS LateMoves
          FROM dbo.FbOddsMove WHERE MatchId IN @ids GROUP BY MatchId, PlayType),
   p AS (
    SELECT a.MatchId, a.PlayType, a.FirstAt, a.LastAt, a.Rounds, a.Options,
           ft.OptionCode AS FCode, ft.OptionName AS FName, ft.Odds AS FOdds,
           lt.OptionCode AS LCode, lt.OptionName AS LName, lt.Odds AS LOdds,
           pf.W AS OW, pf.D AS OD, pf.L AS OL,
           pl.W AS CW, pl.D AS CD, pl.L AS CL
    FROM agg a
    LEFT JOIN ft ON ft.MatchId = a.MatchId AND ft.PlayType = a.PlayType
    LEFT JOIN lt ON lt.MatchId = a.MatchId AND lt.PlayType = a.PlayType
    LEFT JOIN pf ON pf.MatchId = a.MatchId AND pf.PlayType = a.PlayType
    LEFT JOIN pl ON pl.MatchId = a.MatchId AND pl.PlayType = a.PlayType
   )
INSERT INTO dbo.FbMatchTrend
    (MatchId, PlayType, MatchNo, FirstAt, LastAt, Rounds, Options, Moves, BigMoves,
     UpCount, DownCount, FirstTopCode, FirstTopName, FirstTopOdds,
     LastTopCode, LastTopName, LastTopOdds, TopShift,
     OpenWin, OpenDraw, OpenLoss, CurrWin, CurrDraw, CurrLoss,
     FirstMoveAt, LastMoveAt, LateMoves, TrendTag, UpdatedAt)
SELECT p.MatchId, p.PlayType,
       (SELECT MAX(n.MatchNo) FROM dbo.FbJcOddsHistory n
        WHERE n.MatchId = p.MatchId AND n.MatchNo IS NOT NULL),
       p.FirstAt, p.LastAt, p.Rounds, p.Options,
       ISNULL(mv.Moves, 0), ISNULL(mv.BigMoves, 0), ISNULL(mv.UpCount, 0), ISNULL(mv.DownCount, 0),
       p.FCode, p.FName, p.FOdds, p.LCode, p.LName, p.LOdds,
       CASE WHEN p.FCode = p.LCode AND p.FOdds > 1
            THEN (p.LOdds - p.FOdds) / p.FOdds END,
       p.OW, p.OD, p.OL, p.CW, p.CD, p.CL,
       mv.FirstMoveAt, mv.LastMoveAt, ISNULL(mv.LateMoves, 0),
       CASE
           WHEN p.OW > 1 AND p.OD > 1 AND p.OL > 1 AND p.CW > 1 AND p.CD > 1 AND p.CL > 1 THEN
               CASE
                   WHEN ABS((1.0/p.CW) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL))
                          - (1.0/p.OW) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))) < 0.02
                    AND ABS((1.0/p.CD) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL))
                          - (1.0/p.OD) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))) < 0.02
                    AND ABS((1.0/p.CL) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL))
                          - (1.0/p.OL) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))) < 0.02
                   THEN N'无明显迁移'
                   WHEN (1.0/p.CW) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL))
                      - (1.0/p.OW) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))
                      >= (1.0/p.CD) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL)) - (1.0/p.OD) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))
                    AND (1.0/p.CW) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL))
                      - (1.0/p.OW) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))
                      >= (1.0/p.CL) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL)) - (1.0/p.OL) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))
                   THEN N'主胜走强'
                   WHEN (1.0/p.CL) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL))
                      - (1.0/p.OL) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))
                      >= (1.0/p.CD) / ((1.0/p.CW)+(1.0/p.CD)+(1.0/p.CL)) - (1.0/p.OD) / ((1.0/p.OW)+(1.0/p.OD)+(1.0/p.OL))
                   THEN N'客胜走强'
                   ELSE N'平局走强'
               END
           WHEN p.FCode <> p.LCode THEN CONCAT(N'热门档切换 ', p.FName, N'→', p.LName)
       END,
       SYSDATETIME()
FROM p
LEFT JOIN mv ON mv.MatchId = p.MatchId AND mv.PlayType = p.PlayType;";

    /// <summary>
    /// 重算指定场次的净胜球诊断（每场一行）。
    ///
    /// 口径与 db/20_score_diag.sql 的全量回填必须逐字一致，否则历史和新数据接不上。
    /// 唯一差别：这里是 DEL 加 INS，回填是 MERGE——效果等价，增量场景下 DEL 更省。
    ///
    /// 整轮不足 20 档的快照整轮丢掉。新挂的场次先上三五个比分档，
    /// 按那几档归一化会得出 PHome=100%、ExpGd=4.0 这种假值（回填时真出现过）。
    /// </summary>
    private static async Task RefreshScoreDiagAsync(
        SqlConnection conn, SqlTransaction tx, long[] matchIds, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(ScoreDiagSql,
            new { ids = matchIds }, tx, cancellationToken: ct));
    }

    private const string ScoreDiagSql = @"
DELETE FROM dbo.FbScoreDiag WHERE MatchId IN @ids;

;WITH base AS (
    SELECT MatchId, CapturedAt, OptionCode, Odds,
           CASE WHEN CHARINDEX(':', OptionCode) > 0
                THEN CAST(LEFT(OptionCode, CHARINDEX(':', OptionCode) - 1) AS int)
                   - CAST(SUBSTRING(OptionCode, CHARINDEX(':', OptionCode) + 1, 3) AS int)
                WHEN UNICODE(LEFT(OptionCode, 1)) = 32988 THEN 5
                WHEN UNICODE(LEFT(OptionCode, 1)) = 36127 THEN -5
                ELSE 0 END AS gd,
           CASE WHEN CHARINDEX(':', OptionCode) > 0
                THEN CAST(LEFT(OptionCode, CHARINDEX(':', OptionCode) - 1) AS int)
                   + CAST(SUBSTRING(OptionCode, CHARINDEX(':', OptionCode) + 1, 3) AS int)
                ELSE 3 END AS tg
    FROM dbo.FbJcOddsHistory
    WHERE MatchId IN @ids AND PlayType = 'BF' AND Odds > 1
),
cnt AS (
    SELECT MatchId, CapturedAt, COUNT(*) AS n FROM base GROUP BY MatchId, CapturedAt
),
ok AS (
    SELECT b.* FROM base b JOIN cnt c ON c.MatchId = b.MatchId AND c.CapturedAt = b.CapturedAt
    WHERE c.n >= 20
),
norm AS (
    SELECT MatchId, CapturedAt, OptionCode, Odds, gd, tg,
           1.0 / Odds / SUM(1.0 / Odds) OVER (PARTITION BY MatchId, CapturedAt) AS p
    FROM ok
),
per AS (
    SELECT MatchId, CapturedAt,
           SUM(gd * p)                                AS exp_gd,
           SUM(tg * p)                                AS exp_tg,
           SUM(CASE WHEN gd >  0 THEN p ELSE 0 END)   AS p_home,
           SUM(CASE WHEN gd =  0 THEN p ELSE 0 END)   AS p_draw,
           SUM(CASE WHEN gd >= 3 THEN p ELSE 0 END)   AS p3,
           SUM(CASE WHEN gd <= -3 THEN p ELSE 0 END)  AS pm3
    FROM norm
    GROUP BY MatchId, CapturedAt
),
rk AS (
    SELECT *,
           ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt)      AS rf,
           ROW_NUMBER() OVER (PARTITION BY MatchId ORDER BY CapturedAt DESC) AS rl,
           MIN(CapturedAt) OVER (PARTITION BY MatchId) AS fat,
           MAX(CapturedAt) OVER (PARTITION BY MatchId) AS lat,
           COUNT(*)        OVER (PARTITION BY MatchId) AS n
    FROM per
),
fr AS (SELECT * FROM rk WHERE rf = 1),
lr AS (SELECT * FROM rk WHERE rl = 1),
fraw AS (
    SELECT n.MatchId, n.OptionCode, n.Odds, n.gd
    FROM norm n JOIN fr ON fr.MatchId = n.MatchId AND fr.CapturedAt = n.CapturedAt
),
lraw AS (
    SELECT n.MatchId, n.OptionCode, n.Odds, n.gd
    FROM norm n JOIN lr ON lr.MatchId = n.MatchId AND lr.CapturedAt = n.CapturedAt
),
deepdrop AS (
    SELECT l.MatchId,
           SUM(CASE WHEN l.gd <= -3 AND l.Odds <= f.Odds * 0.95 THEN 1 ELSE 0 END) AS neg_drop,
           SUM(CASE WHEN l.gd >=  3 AND l.Odds <= f.Odds * 0.95 THEN 1 ELSE 0 END) AS pos_drop
    FROM lraw l JOIN fraw f ON f.MatchId = l.MatchId AND f.OptionCode = l.OptionCode
    GROUP BY l.MatchId
),
dist AS (
    SELECT n.MatchId, n.gd, SUM(n.p) AS p
    FROM norm n JOIN lr ON lr.MatchId = n.MatchId AND lr.CapturedAt = n.CapturedAt
    GROUP BY n.MatchId, n.gd
),
distj AS (
    SELECT MatchId,
           STRING_AGG(CAST(gd AS varchar(4)) + ':' + CAST(CAST(ROUND(p * 1000, 0) AS int) AS varchar(6)), ',')
               WITHIN GROUP (ORDER BY gd) AS j
    FROM dist GROUP BY MatchId
),
ins AS (
    SELECT l.MatchId,
           (SELECT MAX(n.MatchNo) FROM dbo.FbJcOddsHistory n
            WHERE n.MatchId = l.MatchId AND n.MatchNo IS NOT NULL) AS MatchNo,
           l.n AS Rounds, l.fat AS FirstAt, l.lat AS LastAt,
           l.exp_gd, f.exp_gd AS exp_gd_open, l.exp_gd - f.exp_gd AS exp_gd_move,
           l.exp_tg,
           l.p_home, l.p_draw, 1.0 - l.p_home - l.p_draw AS p_away, f.p_home AS p_home_open,
           l.p3, l.pm3, l.p3 - f.p3 AS p3_move, l.pm3 - f.pm3 AS pm3_move,
           ISNULL(d.neg_drop, 0) AS neg_drop, ISNULL(d.pos_drop, 0) AS pos_drop,
           dj.j AS DistText,
           CASE
               WHEN ISNULL(d.neg_drop,0) >= 3 AND ISNULL(d.pos_drop,0) >= 3 THEN N'两端整批降价'
               WHEN ISNULL(d.neg_drop,0) >= 3 THEN N'客队深档整批降价'
               WHEN ISNULL(d.pos_drop,0) >= 3 THEN N'主队深档整批降价'
               WHEN l.p3 - f.p3 >= 0.01 AND l.pm3 - f.pm3 >= 0.01 THEN N'两端加厚'
               WHEN l.p3 - f.p3 >= 0.01 THEN N'主队深档加价'
               WHEN l.pm3 - f.pm3 >= 0.01 THEN N'客队深档加价'
               WHEN l.p3 - f.p3 <= -0.01 AND l.pm3 - f.pm3 <= -0.01 THEN N'两端削薄'
               WHEN l.p3 - f.p3 <= -0.01 THEN N'主队深档减价'
               WHEN l.pm3 - f.pm3 <= -0.01 THEN N'客队深档减价'
               ELSE N'尾部未动'
           END AS TailTag
    FROM lr l
    JOIN fr f ON f.MatchId = l.MatchId
    LEFT JOIN deepdrop d ON d.MatchId = l.MatchId
    LEFT JOIN distj dj ON dj.MatchId = l.MatchId
)
INSERT INTO dbo.FbScoreDiag
    (MatchId, MatchNo, Rounds, FirstAt, LastAt,
     ExpGd, ExpGdOpen, ExpGdMove, ExpTotal,
     PHome, PDraw, PAway, PHomeOpen,
     P3Plus, PMinus3, P3PlusMove, PMinus3Move,
     DeepNegDrop, DeepPosDrop, DistText, TailTag, UpdatedAt)
SELECT MatchId, MatchNo, Rounds, FirstAt, LastAt,
       exp_gd, exp_gd_open, exp_gd_move, exp_tg,
       p_home, p_draw, p_away, p_home_open,
       p3, pm3, p3_move, pm3_move,
       neg_drop, pos_drop, DistText, TailTag, SYSDATETIME()
FROM ins;";

    /// <summary>本批场次「上一次采样」的价格，键 = MatchNo|PlayType|OptionCode。</summary>
    private static async Task<Dictionary<string, (decimal Odds, DateTime At)>> GetPrevSampledAsync(
        SqlConnection conn, SqlTransaction tx, string[] matchNos, DateTime before, CancellationToken ct)
    {
        var map = new Dictionary<string, (decimal, DateTime)>(StringComparer.Ordinal);
        if (matchNos.Length == 0) return map;

        var rows = await conn.QueryAsync<(string MatchNo, string PlayType, string OptionCode, decimal Odds, DateTime CapturedAt)>(
            new CommandDefinition(
                @"WITH t AS (
                      SELECT MatchNo, PlayType, OptionCode, MAX(CapturedAt) AS lastAt
                      FROM dbo.FbJcOddsHistory
                      WHERE MatchNo IN @nos AND CapturedAt < @before
                      GROUP BY MatchNo, PlayType, OptionCode
                  )
                  SELECT h.MatchNo, h.PlayType, h.OptionCode, h.Odds, h.CapturedAt
                  FROM dbo.FbJcOddsHistory h
                  JOIN t ON t.MatchNo = h.MatchNo AND t.PlayType = h.PlayType
                        AND t.OptionCode = h.OptionCode AND t.lastAt = h.CapturedAt",
                new { nos = matchNos, before }, tx, cancellationToken: ct));

        foreach (var r in rows)
            map[$"{r.MatchNo}|{r.PlayType}|{r.OptionCode}"] = (r.Odds, r.CapturedAt);
        return map;
    }

    /// <summary>
    /// 把本批超出阈值的变盘写进 FbOddsMove。只记事实：几点、哪档、从多少到多少、距开赛多久。
    /// 不判断方向、不拦注——11-04 的教训是缺「记录」，不是缺「判断」。
    /// </summary>
    private static async Task<int> RecordMovesAsync(
        SqlConnection conn, SqlTransaction tx, IReadOnlyList<JcOddsHistoryPoint> points,
        DateTime capturedAt, IReadOnlyDictionary<string, (decimal Odds, DateTime At)> prev, CancellationToken ct)
    {
        var written = 0;
        foreach (var p in points)
        {
            if (p.MatchId is null || string.IsNullOrEmpty(p.MatchNo) || p.Odds <= 1) continue;
            if (!prev.TryGetValue($"{p.MatchNo}|{p.PlayType}|{p.OptionCode}", out var old)) continue;
            if (old.Odds <= 1 || old.Odds == p.Odds) continue;

            var pct = (p.Odds - old.Odds) / old.Odds;
            if (Math.Abs(pct) < MoveThreshold) continue;

            written += await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbOddsMove
                      (MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                       PlayType, OptionCode, OptionName, PrevOdds, CurrOdds, ChangePct, PrevAt, MoveAt, MinutesToKick)
                  SELECT m.MatchId, @no, m.LeagueName, m.HomeTeamName, m.AwayTeamName, m.MatchTime,
                         @pt, @code, @name, @prev, @odds, @pct, @prevAt, @at,
                         DATEDIFF(minute, @at, m.MatchTime)
                  FROM dbo.FbMatch m
                  WHERE m.MatchId = @mid
                    AND NOT EXISTS (SELECT 1 FROM dbo.FbOddsMove d
                                    WHERE d.MatchId = @mid AND d.PlayType = @pt
                                      AND d.OptionCode = @code AND d.MoveAt = @at)",
                new
                {
                    mid = p.MatchId, no = p.MatchNo, pt = p.PlayType, code = p.OptionCode,
                    name = p.OptionName, prev = old.Odds, odds = p.Odds, pct,
                    prevAt = old.At, at = capturedAt
                }, tx, cancellationToken: ct));
        }
        return written;
    }

    /// <summary>
    /// 按场次号取竞彩固定奖金的最新水位（当前值 + 相对上一次采样的涨跌）。
    /// 返回结构按玩法分组，每个选项给出 odds 与 delta。
    /// </summary>
    public async Task<object?> GetWaterAsync(string matchNo, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        // 最近两次采样时间点
        var times = (await conn.QueryAsync<DateTime>(new CommandDefinition(
            @"SELECT DISTINCT TOP 2 CapturedAt FROM dbo.FbJcOddsHistory
              WHERE MatchNo = @no ORDER BY CapturedAt DESC", new { no = matchNo }, cancellationToken: ct))).ToList();
        if (times.Count == 0) return null;

        var latestAt = times[0];
        var prevAt = times.Count > 1 ? times[1] : (DateTime?)null;

        // 最新一次采样的所有选项
        var latest = await conn.QueryAsync<JcOddsHistoryPoint>(new CommandDefinition(
            @"SELECT * FROM dbo.FbJcOddsHistory WHERE MatchNo = @no AND CapturedAt = @at",
            new { no = matchNo, at = latestAt }, cancellationToken: ct));

        // 上一次采样的选项（算涨跌）
        Dictionary<string, decimal> prevMap = new();
        if (prevAt is not null)
        {
            var prev = await conn.QueryAsync<JcOddsHistoryPoint>(new CommandDefinition(
                @"SELECT * FROM dbo.FbJcOddsHistory WHERE MatchNo = @no AND CapturedAt = @at",
                new { no = matchNo, at = prevAt }, cancellationToken: ct));
            foreach (var p in prev)
                prevMap[$"{p.PlayType}|{p.OptionCode}"] = p.Odds;
        }

        // 按玩法分组
        var groups = latest
            .GroupBy(p => p.PlayType)
            .Select(g => new
            {
                playType = g.Key,
                playName = PlayName(g.Key),
                options = g.Select(p =>
                {
                    var key = $"{p.PlayType}|{p.OptionCode}";
                    decimal? delta = prevMap.TryGetValue(key, out var prevOdds)
                        ? decimal.Round(p.Odds - prevOdds, 3)
                        : null;
                    return new
                    {
                        code = p.OptionCode,
                        name = p.OptionName,
                        odds = p.Odds,
                        delta
                    };
                }).ToList()
            }).ToList();

        return new
        {
            matchNo,
            capturedAt = latestAt,
            prevCapturedAt = prevAt,
            groups
        };
    }

    /// <summary>每场每个选项取最近一次大于 1 的赔率。1 是完场后的占位，不当成报价。</summary>
    public async Task<List<JcOddsHistoryPoint>> GetLatestAsync(IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<JcOddsHistoryPoint>(new CommandDefinition(
            @"SELECT MatchId, PlayType, OptionCode, OptionName, Odds
              FROM (
                  SELECT MatchId, PlayType, OptionCode, OptionName, Odds,
                         ROW_NUMBER() OVER (PARTITION BY MatchId, PlayType, OptionCode ORDER BY CapturedAt DESC) AS rn
                  FROM dbo.FbJcOddsHistory
                  WHERE MatchId IN @ids AND Odds > 1
              ) t WHERE rn = 1",
            new { ids }, cancellationToken: ct))).ToList();
    }

    /// <summary>各场胜平负的最新价。键是 MatchId，值是胜/平/负。</summary>
    public Task<Dictionary<long, (decimal Win, decimal Draw, decimal Loss)>> GetLatestSpfAsync(
        IEnumerable<long> matchIds, CancellationToken ct = default)
        => GetLatestPlayAsync(matchIds, "SPF", ct);

    /// <summary>各场让球胜平负的最新价。键是 MatchId，值是让胜/让平/让负。</summary>
    public Task<Dictionary<long, (decimal Win, decimal Draw, decimal Loss)>> GetLatestNspfAsync(
        IEnumerable<long> matchIds, CancellationToken ct = default)
        => GetLatestPlayAsync(matchIds, "NSPF", ct);

    private async Task<Dictionary<long, (decimal Win, decimal Draw, decimal Loss)>> GetLatestPlayAsync(
        IEnumerable<long> matchIds, string playType, CancellationToken ct)
    {
        var ids = matchIds.Distinct().ToArray();
        var map = new Dictionary<long, (decimal Win, decimal Draw, decimal Loss)>();
        if (ids.Length == 0) return map;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long MatchId, string OptionCode, decimal Odds)>(new CommandDefinition(
            @"SELECT MatchId, OptionCode, Odds
              FROM (
                  SELECT MatchId, OptionCode, Odds,
                         ROW_NUMBER() OVER (PARTITION BY MatchId, OptionCode ORDER BY CapturedAt DESC) AS rn
                  FROM dbo.FbJcOddsHistory
                  WHERE PlayType = @play AND MatchId IN @ids AND OptionCode IN ('3','1','0') AND Odds > 1
              ) t
              WHERE rn = 1",
            new { ids, play = playType }, cancellationToken: ct));

        foreach (var g in rows.GroupBy(r => r.MatchId))
        {
            decimal win = 0, draw = 0, loss = 0;
            foreach (var row in g)
            {
                if (row.OptionCode == "3") win = row.Odds;
                else if (row.OptionCode == "1") draw = row.Odds;
                else if (row.OptionCode == "0") loss = row.Odds;
            }
            if (win > 1 && draw > 1 && loss > 1)
                map[g.Key] = (win, draw, loss);
        }
        return map;
    }

    /// <summary>北单开奖页上的 SP。同一选项价格没变就不再写一行。</summary>
    public async Task InsertChangedAsync(IReadOnlyList<JcOddsHistoryPoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0) return;
        var at = DateTime.Now;
        await using var conn = await _factory.OpenAsync(ct);
        foreach (var p in points)
        {
            if (p.MatchId is null || p.Odds <= 1) continue;
            await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbJcOddsHistory (MatchId, MatchNo, PlayType, OptionCode, OptionName, Odds, CapturedAt)
                  SELECT @mid, @no, @pt, @code, @name, @odds, @at
                  WHERE NOT EXISTS (
                      SELECT 1 FROM dbo.FbJcOddsHistory
                      WHERE MatchId = @mid AND PlayType = @pt AND OptionCode = @code AND Odds = @odds)",
                new
                {
                    mid = p.MatchId, no = p.MatchNo, pt = p.PlayType, code = p.OptionCode,
                    name = p.OptionName, odds = p.Odds, at
                }, cancellationToken: ct));
        }
    }

    private static string PlayName(string playType) => playType switch
    {
        JcPlayTypes.Nspf => "让球胜平负",
        JcPlayTypes.Spf => "胜平负",
        JcPlayTypes.Bf => "全场比分",
        JcPlayTypes.Jq => "总进球",
        JcPlayTypes.Bq => "半全场",
        _ => playType
    };
}
