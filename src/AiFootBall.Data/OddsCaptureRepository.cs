using AiFootBall.Core.Models;
using Dapper;

namespace AiFootBall.Data;

/// <summary>
/// 逐场采集台账与走向读取。
///
/// 写入在两个地方，都跟快照同事务：
///   · <see cref="JcOddsRepository.SaveSnapshotAsync"/> —— FbOddsScanLog（一轮一行）+ FbMatchTrend（每场每玩法）
///
/// 这个类只读。它回答三个问题：
///   1. 今天到底采了几轮、哪几轮之间断了（台账 + 断档）
///   2. 每场每个玩法从开盘到现在往哪走了（首末热门档、去水概率迁移）
///   3. 变盘发生在什么时候、离比赛还有多久（临场资金 vs 提前布局）
/// </summary>
public sealed class OddsCaptureRepository
{
    /// <summary>
    /// 断档判定阈值（分钟）。竞彩采样排的是 2 分钟一轮，实际受 500 接口节流影响会慢一些；
    /// 超过 15 分钟视为「这一段没采到」。取 15 而不是 3，是因为 5 分钟一轮是常态，
    /// 卡太紧会把正常节流全标成故障，标红了就没人看了。
    /// </summary>
    public const int GapThresholdMinutes = 15;

    private readonly SqlConnectionFactory _factory;

    public OddsCaptureRepository(SqlConnectionFactory factory) => _factory = factory;

    /// <summary>某天的采集台账，按时间倒序。带相邻两轮的间隔，间隔超阈值的标出来。</summary>
    public async Task<ScanLogResult> GetScanLogAsync(DateOnly day, string source = "jc", CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var rows = (await conn.QueryAsync<OddsScanRow>(new CommandDefinition(
            @"SELECT ScanId, Source, CapturedAt, MatchCount, OptionCount, MoveCount,
                     ElapsedMs, Status, Note
              FROM dbo.FbOddsScanLog
              WHERE Source = @source AND CapturedAt >= @from AND CapturedAt < @to
              ORDER BY CapturedAt",
            new
            {
                source,
                from = day.ToDateTime(TimeOnly.MinValue),
                to = day.AddDays(1).ToDateTime(TimeOnly.MinValue)
            }, cancellationToken: ct))).ToList();

        // 断档：相邻两轮的时间差。第一轮之前不算（不知道进程几点起的）。
        var gaps = new List<ScanGap>();
        for (var i = 1; i < rows.Count; i++)
        {
            var minutes = (int)Math.Round((rows[i].CapturedAt - rows[i - 1].CapturedAt).TotalMinutes);
            rows[i].GapMinutes = minutes;
            if (minutes > GapThresholdMinutes)
                gaps.Add(new ScanGap
                {
                    From = rows[i - 1].CapturedAt,
                    To = rows[i].CapturedAt,
                    Minutes = minutes
                });
        }

        var ok = rows.Count(r => r.Status == "ok");
        var err = rows.Count(r => r.Status != "ok");
        var covered = rows.Where(r => r.GapMinutes is null or <= GapThresholdMinutes)
                          .Sum(r => r.GapMinutes ?? 0);

        return new ScanLogResult
        {
            Day = day,
            Source = source,
            Rounds = rows.Count,
            OkRounds = ok,
            ErrorRounds = err,
            FirstAt = rows.Count > 0 ? rows[0].CapturedAt : null,
            LastAt = rows.Count > 0 ? rows[^1].CapturedAt : null,
            CoveredMinutes = covered,
            GapThresholdMinutes = GapThresholdMinutes,
            Gaps = gaps,
            Rows = rows
        };
    }

    /// <summary>
    /// 某天（按开赛时间）所有场次的走向，一行一个「场 × 玩法」。
    /// 表格上看是每场一段，前端会按 MatchId 分组。
    /// </summary>
    public async Task<List<MatchTrendRow>> GetTrendByDayAsync(
        DateOnly day, string? playType = null, int take = 600,
        bool openOnly = false, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<MatchTrendRow>(new CommandDefinition(
            @"SELECT TOP (@take) t.*, m.LeagueName, m.HomeTeamName, m.AwayTeamName, m.MatchTime,
                     m.HomeScore, m.AwayScore, m.Status AS MatchStatus, m.ResultSpf,
                     DATEDIFF(minute, SYSDATETIME(), m.MatchTime) AS MinutesToKick,
                     ah.AhOpen, ah.AhLast
              FROM dbo.FbMatchTrend t
              JOIN dbo.FbMatch m ON m.MatchId = t.MatchId
              OUTER APPLY (SELECT MAX(CASE WHEN o.IsOpening = 1 THEN o.Handicap END) AhOpen,
                                  MAX(CASE WHEN o.IsOpening = 0 THEN o.Handicap END) AhLast
                           FROM dbo.FbOdds o
                           WHERE o.MatchId = m.MatchId AND o.OddsType = 'AH' AND o.CompanyId = 0) ah
              WHERE CONVERT(date, m.MatchTime) = @day
                AND (@play IS NULL OR t.PlayType = @play)
                AND (@openOnly = 0 OR m.Status <> 3)
              ORDER BY m.MatchTime, t.MatchId, t.PlayType",
            new { day = day.ToDateTime(TimeOnly.MinValue), play = playType, take, openOnly }, cancellationToken: ct));
        var list = rows.ToList();
        foreach (var r in list) r.TagHit = JudgeTag(r);
        return list;
    }

    /// <summary>
    /// 按场次集合取 SPF「开盘→现价」水位（技能11升水否决用，2026-11-07）。
    /// 直接读 FbMatchTrend（jc-odds-sample 每轮刷新），OpenWin/CurrWin 任一缺失的场不进字典。
    /// </summary>
    public async Task<Dictionary<long, (decimal? Open, decimal? Curr)>> GetSpfWaterAsync(
        IReadOnlyCollection<long> matchIds, CancellationToken ct = default)
    {
        if (matchIds.Count == 0) return new();
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long MatchId, decimal? OpenWin, decimal? CurrWin)>(new CommandDefinition(
            @"SELECT MatchId, OpenWin, CurrWin
              FROM dbo.FbMatchTrend
              WHERE PlayType = 'SPF' AND MatchId IN @ids",
            new { ids = matchIds }, cancellationToken: ct));
        return rows
            .Where(r => r.OpenWin is > 1m && r.CurrWin is > 1m)
            .ToDictionary(r => r.MatchId, r => ((decimal?)r.OpenWin, (decimal?)r.CurrWin));
    }

    /// <summary>
    /// 取每场「竞彩 + 必发」两侧首末价，供 <see cref="PriceDivergence"/> 判单边定价背离
    /// （2026-11-08 加，曼城复盘落地）。
    ///
    /// 口径必须与回测严格一致，否则阈值 10%/3% 就是拍脑袋：
    ///   · 竞彩：dbo.FbJcOddsHistory 的 PlayType='spf'，按 (MatchId, OptionCode) 取最早/最晚 CapturedAt 的赔率。
    ///     这里**不用** FbMatchTrend 的 OpenWin/CurrWin——回测算的是这张原始表，换表就换了口径。
    ///   · 必发：dbo.FbExchange 的第一条/最后一条，**且只取开赛前**（CapturedAt &lt;= MatchTime）。
    ///     必发会记到赛中，不掐这道线等于把比分喂进信号里，回测和生产会得出相反结论。
    ///
    /// 任一侧缺价就留 null，由 <see cref="PriceDivergence.Judge"/> 跳过那一侧，不猜。
    /// </summary>
    public async Task<Dictionary<long, PriceDivergence.PriceSpan>> GetPriceSpanAsync(
        IReadOnlyCollection<long> matchIds, CancellationToken ct = default)
    {
        if (matchIds.Count == 0) return new();
        await using var conn = await _factory.OpenAsync(ct);
        // 显式转数组再传参数：Dapper 只在参数值是数组/List 时才展开 IN @ids，
        // 传 IReadOnlyCollection 时某些版本会把它当成单个标量、查询静默返回 0 行
        // （2026-11-08 实测栽过一次：日志 spans=0/30，SQL 单跑却有数据）。
        var ids = matchIds.Distinct().ToArray();

        var jcRows = (await conn.QueryAsync<PriceSpanRow>(
            new CommandDefinition(
                @"SELECT t.MatchId,
                         MAX(CASE WHEN t.OptionCode='3' AND t.rn_a=1 THEN t.Odds END) AS OpenHome,
                         MAX(CASE WHEN t.OptionCode='3' AND t.rn_d=1 THEN t.Odds END) AS CurrHome,
                         MAX(CASE WHEN t.OptionCode='0' AND t.rn_a=1 THEN t.Odds END) AS OpenAway,
                         MAX(CASE WHEN t.OptionCode='0' AND t.rn_d=1 THEN t.Odds END) AS CurrAway
                  FROM (
                      SELECT MatchId, OptionCode, Odds,
                             ROW_NUMBER() OVER (PARTITION BY MatchId, OptionCode ORDER BY CapturedAt) rn_a,
                             ROW_NUMBER() OVER (PARTITION BY MatchId, OptionCode ORDER BY CapturedAt DESC) rn_d
                      FROM dbo.FbJcOddsHistory
                      WHERE MatchId IN @ids AND PlayType = 'spf' AND Odds > 1
                  ) t
                  WHERE t.rn_a = 1 OR t.rn_d = 1
                  GROUP BY t.MatchId",
                new { ids }, cancellationToken: ct))).ToList();

        var exRows = (await conn.QueryAsync<PriceSpanRow>(
            new CommandDefinition(
                @"SELECT e.MatchId,
                         MAX(CASE WHEN e.rn_a=1 THEN e.PriceHome END) AS OpenHome,
                         MAX(CASE WHEN e.rn_d=1 THEN e.PriceHome END) AS CurrHome,
                         MAX(CASE WHEN e.rn_a=1 THEN e.PriceAway END) AS OpenAway,
                         MAX(CASE WHEN e.rn_d=1 THEN e.PriceAway END) AS CurrAway
                  FROM (
                      SELECT x.MatchId, x.PriceHome, x.PriceAway,
                             ROW_NUMBER() OVER (PARTITION BY x.MatchId ORDER BY x.CapturedAt) rn_a,
                             ROW_NUMBER() OVER (PARTITION BY x.MatchId ORDER BY x.CapturedAt DESC) rn_d
                      FROM dbo.FbExchange x
                      JOIN dbo.FbMatch m ON m.MatchId = x.MatchId
                      WHERE x.MatchId IN @ids AND x.TotalVolume > 0
                        AND x.PriceHome > 1 AND x.PriceAway > 1
                        AND x.CapturedAt <= m.MatchTime
                  ) e
                  GROUP BY e.MatchId",
                new { ids }, cancellationToken: ct))).ToList();

        var jc = jcRows.ToDictionary(r => r.MatchId, r => r);
        var ex = exRows.ToDictionary(r => r.MatchId, r => r);

        var result = new Dictionary<long, PriceDivergence.PriceSpan>(jc.Count);
        foreach (var (mid, j) in jc)
        {
            ex.TryGetValue(mid, out var x);
            result[mid] = new PriceDivergence.PriceSpan(
                j.OpenHome, j.CurrHome, j.OpenAway, j.CurrAway,
                x.OpenHome, x.CurrHome, x.OpenAway, x.CurrAway);
        }
        return result;
    }

    /// <summary>
    /// GetPriceSpanAsync 的行类型。用显式 POCO 而不是 ValueTuple——
    /// Dapper 的 ValueTuple 映射在某些版本上只按位置匹配、且 5 个元素时容易被静默吞掉，
    /// 而这里外面还套着 try/catch（可选信号不能拖死出榜），映射失败会变成「空字典 + 无报错」，
    /// 表现就是「这功能上线了但一天都没触发过」。POCO 按列名映射，没有这个坑。
    /// 用 internal 而不是 private：Dapper 要能反射实例化这个类型。
    /// </summary>
    internal sealed class PriceSpanRow
    {
        public long MatchId { get; set; }
        public decimal? OpenHome { get; set; }
        public decimal? CurrHome { get; set; }
        public decimal? OpenAway { get; set; }
        public decimal? CurrAway { get; set; }
    }

    /// <summary>
    /// 取各队「本场之前」的近期赛程与状态（2026-11-08 加，曼城复盘的体能项）。
    ///
    /// 只回答三个问题：上一场什么时候、上一场是什么赛事、最近三场胜平负。
    /// 三连败/休息小时数/欧战标记都由 <see cref="TeamForm"/> 自己算，仓储不掺业务判断。
    ///
    /// 队名口径与画像一致：键是数据源全称，调用方先用 ScorelineModel.Resolve 解析。
    /// 回看窗口给 150 天——再久的前一场对「体能」已经没有意义，但用来判三连败够了；
    /// 窗口太大会把整张 FbMatch 拉进来。
    /// </summary>
    public async Task<Dictionary<string, TeamForm>> GetTeamFormAsync(
        IEnumerable<string> teamNames, DateTime reference, CancellationToken ct = default)
    {
        var names = teamNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        // 主客两行合并成「该队的比赛」，再由内存按时间倒序取前 4 场。
        // 不在 SQL 里做窗口函数：球队数不多（每天几十支），内存排序更好读，也不用担心方言。
        var rows = await conn.QueryAsync<TeamFormRow>(new CommandDefinition(
            @"SELECT LTRIM(RTRIM(m.HomeTeamName)) AS TeamName,
                     m.MatchTime, LTRIM(RTRIM(m.LeagueName)) AS LeagueName,
                     m.HomeScore AS Gf, m.AwayScore AS Ga
              FROM dbo.FbMatch m
              WHERE m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                AND m.MatchTime < @reference
                AND m.MatchTime >= DATEADD(day, -150, @reference)
                AND LTRIM(RTRIM(m.HomeTeamName)) IN @names
              UNION ALL
              SELECT LTRIM(RTRIM(m.AwayTeamName)) AS TeamName,
                     m.MatchTime, LTRIM(RTRIM(m.LeagueName)) AS LeagueName,
                     m.AwayScore AS Gf, m.HomeScore AS Ga
              FROM dbo.FbMatch m
              WHERE m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                AND m.MatchTime < @reference
                AND m.MatchTime >= DATEADD(day, -150, @reference)
                AND LTRIM(RTRIM(m.AwayTeamName)) IN @names",
            new { reference, names }, cancellationToken: ct));

        var result = new Dictionary<string, TeamForm>(StringComparer.Ordinal);
        foreach (var g in rows.GroupBy(r => r.TeamName, StringComparer.Ordinal))
        {
            var ordered = g.OrderByDescending(r => r.MatchTime).Take(4).ToList();
            var last = ordered[0];
            int? Mark(int idx) => idx < ordered.Count
                ? ordered[idx].Gf > ordered[idx].Ga ? 1 : ordered[idx].Gf == ordered[idx].Ga ? 0 : -1
                : (int?)null;

            result[g.Key] = new TeamForm
            {
                TeamName = g.Key,
                LastMatchTime = last.MatchTime,
                LastLeague = last.LeagueName,
                Last1 = Mark(0),
                Last2 = Mark(1),
                Last3 = Mark(2)
            };
        }
        return result;
    }

    /// <summary>近期赛程的行载体。显式 POCO，不用 ValueTuple——Dapper 对元组的列名映射不可靠。</summary>
    private sealed class TeamFormRow
    {
        public string TeamName { get; set; } = "";
        public DateTime MatchTime { get; set; }
        public string? LeagueName { get; set; }
        public int Gf { get; set; }
        public int Ga { get; set; }
    }

    /// <summary>
    /// 按队名取俱乐部画像（2026-11-07，dbo.FbTeamProfile，db/21_team_profile.sql 回填）。
    /// 键 = 数据源全称队名——调用方必须先用 ScorelineModel.Resolve 把竞彩简称解析成全称再查。
    /// minGames 过滤样本太小的队（巴黎全历史才 65 场，画像不可信）。
    /// </summary>
    public async Task<Dictionary<string, TeamProfile>> GetTeamProfilesAsync(
        IEnumerable<string> teamNames, int minGames = 150, CancellationToken ct = default)
    {
        var names = teamNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        if (names.Count == 0) return new();
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<TeamProfile>(new CommandDefinition(
            @"SELECT TeamName, Games, ReversalRate, CleanSheetRate, HalfDrawTurnRate
              FROM dbo.FbTeamProfile
              WHERE TeamName IN @names AND Games >= @minGames",
            new { names, minGames }, cancellationToken: ct));
        return rows.ToDictionary(r => r.TeamName, r => r, StringComparer.Ordinal);
    }

    /// <summary>
    /// 按开赛时间范围取走向（[from, to)），一行一个「场 × 玩法」。
    /// 竞彩档期与自然日错位：周四档欧战多在次日凌晨开赛，单看「开赛日=今天」会漏掉整批在售场次。
    /// 所以「今天在售」用 [今天, 后天) 窗口；显式传 date 才走 <see cref="GetTrendByDayAsync"/> 精确单日（复盘用）。
    /// </summary>
    public async Task<List<MatchTrendRow>> GetTrendByRangeAsync(
        DateTime from, DateTime to, string? playType = null, int take = 600,
        bool openOnly = false, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<MatchTrendRow>(new CommandDefinition(
            @"SELECT TOP (@take) t.*, m.LeagueName, m.HomeTeamName, m.AwayTeamName, m.MatchTime,
                     m.HomeScore, m.AwayScore, m.Status AS MatchStatus, m.ResultSpf,
                     DATEDIFF(minute, SYSDATETIME(), m.MatchTime) AS MinutesToKick,
                     ah.AhOpen, ah.AhLast
              FROM dbo.FbMatchTrend t
              JOIN dbo.FbMatch m ON m.MatchId = t.MatchId
              OUTER APPLY (SELECT MAX(CASE WHEN o.IsOpening = 1 THEN o.Handicap END) AhOpen,
                                  MAX(CASE WHEN o.IsOpening = 0 THEN o.Handicap END) AhLast
                           FROM dbo.FbOdds o
                           WHERE o.MatchId = m.MatchId AND o.OddsType = 'AH' AND o.CompanyId = 0) ah
              WHERE m.MatchTime >= @from AND m.MatchTime < @to
                AND (@play IS NULL OR t.PlayType = @play)
                AND (@openOnly = 0 OR m.Status <> 3)
              ORDER BY m.MatchTime, t.MatchId, t.PlayType",
            new { from, to, play = playType, take, openOnly }, cancellationToken: ct));
        var list = rows.ToList();
        foreach (var r in list) r.TagHit = JudgeTag(r);
        return list;
    }

    /// <summary>
    /// 单场走向：每玩法一行 + 该场完整变盘序列。
    /// 变盘序列按时间正序——要看「方向是怎么一步步走出来的」，倒序会把结局摆在开头。
    /// </summary>
    public async Task<MatchTrendDetail?> GetTrendByMatchAsync(long matchId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var rows = (await conn.QueryAsync<MatchTrendRow>(new CommandDefinition(
            @"SELECT t.*, m.LeagueName, m.HomeTeamName, m.AwayTeamName, m.MatchTime,
                     m.HomeScore, m.AwayScore, m.Status AS MatchStatus, m.ResultSpf,
                     DATEDIFF(minute, SYSDATETIME(), m.MatchTime) AS MinutesToKick,
                     ah.AhOpen, ah.AhLast
              FROM dbo.FbMatchTrend t
              JOIN dbo.FbMatch m ON m.MatchId = t.MatchId
              OUTER APPLY (SELECT MAX(CASE WHEN o.IsOpening = 1 THEN o.Handicap END) AhOpen,
                                  MAX(CASE WHEN o.IsOpening = 0 THEN o.Handicap END) AhLast
                           FROM dbo.FbOdds o
                           WHERE o.MatchId = m.MatchId AND o.OddsType = 'AH' AND o.CompanyId = 0) ah
              WHERE t.MatchId = @matchId
              ORDER BY t.PlayType",
            new { matchId }, cancellationToken: ct))).ToList();

        foreach (var r in rows) r.TagHit = JudgeTag(r);

        if (rows.Count == 0) return null;

        var moves = (await conn.QueryAsync<OddsMoveRow>(new CommandDefinition(
            @"SELECT MoveId, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PlayType, OptionCode, OptionName, PrevOdds, CurrOdds, ChangePct,
                     PrevAt, MoveAt, MinutesToKick
              FROM dbo.FbOddsMove
              WHERE MatchId = @matchId
              ORDER BY MoveAt, PlayType, OptionCode",
            new { matchId }, cancellationToken: ct))).ToList();

        var head = rows[0];
        return new MatchTrendDetail
        {
            MatchId = matchId,
            MatchNo = head.MatchNo,
            LeagueName = head.LeagueName,
            HomeTeam = head.HomeTeamName,
            AwayTeam = head.AwayTeamName,
            MatchTime = head.MatchTime,
            MinutesToKick = head.MinutesToKick,
            Plays = rows,
            Moves = moves
        };
    }

    /// <summary>
    /// 标签 → 它指向的赛果代码（3 主胜 / 1 平 / 0 客胜）。识别不出返回 null。
    ///
    /// 只对胜平负玩法成立。「平局走强」在用让球盘（NSPF）上指的是让球平，套 ResultSpf 会算错，
    /// 所以调用方必须先确认 PlayType=SPF。
    /// </summary>
    public static string? SideOfTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        if (tag.Contains("主胜")) return "3";
        if (tag.Contains("客胜")) return "0";
        if (tag.Contains("平局")) return "1";
        return null;
    }

    /// <summary>标签方向与赛果是否一致。未完赛、非 SPF、标签是「热门档切换」这类无方向文案时都返回 null。</summary>
    public static bool? JudgeTag(MatchTrendRow r)
    {
        if (r.MatchStatus is not 3) return null;
        if (!string.Equals(r.PlayType, "SPF", StringComparison.OrdinalIgnoreCase)) return null;
        if (r.ResultSpf is not ("3" or "1" or "0")) return null;
        var side = SideOfTag(r.TrendTag);
        return side is null ? null : side == r.ResultSpf;
    }

    /// <summary>
    /// 近 N 天的走向标签分布，并带回每个标签的实际命中率。
    ///
    /// 命中率是拿「标签指向的那一边」直接对赛果，不看赔率、不看水位。
    /// 这是标签唯一的验收口径——如果某个标签长期命中率低于基线，它就不该被当成方向读。
    /// </summary>
    public async Task<List<TrendTagStat>> GetTagDistributionAsync(int days = 30, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<TrendTagStat>(new CommandDefinition(
            @"SELECT ISNULL(t.TrendTag, N'(未判定)') AS Tag,
                     COUNT(*) AS Plays,
                     COUNT(DISTINCT t.MatchId) AS Matches,
                     AVG(CAST(t.Moves AS DECIMAL(9,2))) AS AvgMoves,
                     SUM(CASE WHEN m.Status = 3 AND s.Side IS NOT NULL THEN 1 ELSE 0 END) AS Settled,
                     CAST(AVG(CASE WHEN m.Status = 3 AND s.Side IS NOT NULL
                                   THEN CASE WHEN m.ResultSpf = s.Side THEN 1.0 ELSE 0.0 END END)
                          AS DECIMAL(5,3)) AS HitRate
              FROM dbo.FbMatchTrend t
              JOIN dbo.FbMatch m ON m.MatchId = t.MatchId
              CROSS APPLY (SELECT CASE WHEN t.TrendTag LIKE N'%主胜%' THEN '3'
                                       WHEN t.TrendTag LIKE N'%客胜%' THEN '0'
                                       WHEN t.TrendTag LIKE N'%平局%' THEN '1' END AS Side) s
              WHERE m.MatchTime >= DATEADD(day, -1 * @days, CAST(GETDATE() AS date))
                AND t.PlayType = 'SPF'
              GROUP BY ISNULL(t.TrendTag, N'(未判定)')
              ORDER BY COUNT(*) DESC",
            new { days }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// 某天所有场次的净胜球诊断，按开赛时间正序。
    ///
    /// 只读一个已经算好的标量行，不碰 124 万行明细——页面刷新时每场都要用。
    /// </summary>
    public async Task<List<ScoreDiagRow>> GetScoreDiagByDayAsync(
        DateOnly day, int take = 600, bool openOnly = false, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ScoreDiagRow>(new CommandDefinition(
            @"SELECT TOP (@take) d.*, m.LeagueName, m.HomeTeamName, m.AwayTeamName, m.MatchTime,
                     DATEDIFF(minute, SYSDATETIME(), m.MatchTime) AS MinutesToKick
              FROM dbo.FbScoreDiag d
              JOIN dbo.FbMatch m ON m.MatchId = d.MatchId
              WHERE CONVERT(date, m.MatchTime) = @day
                AND (@openOnly = 0 OR m.Status <> 3)
              ORDER BY m.MatchTime, d.MatchId",
            new { day = day.ToDateTime(TimeOnly.MinValue), take, openOnly }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>按开赛时间范围取净胜球诊断（[from, to)），与 <see cref="GetTrendByRangeAsync"/> 同一个销售窗口口径。</summary>
    public async Task<List<ScoreDiagRow>> GetScoreDiagByRangeAsync(
        DateTime from, DateTime to, int take = 600, bool openOnly = false, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ScoreDiagRow>(new CommandDefinition(
            @"SELECT TOP (@take) d.*, m.LeagueName, m.HomeTeamName, m.AwayTeamName, m.MatchTime,
                     DATEDIFF(minute, SYSDATETIME(), m.MatchTime) AS MinutesToKick
              FROM dbo.FbScoreDiag d
              JOIN dbo.FbMatch m ON m.MatchId = d.MatchId
              WHERE m.MatchTime >= @from AND m.MatchTime < @to
                AND (@openOnly = 0 OR m.Status <> 3)
              ORDER BY m.MatchTime, d.MatchId",
            new { from, to, take, openOnly }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>单场净胜球诊断。没有比分盘快照的场次返回 null（不是 0，0 会被误读成「平局」）。</summary>
    public async Task<ScoreDiagRow?> GetScoreDiagAsync(long matchId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<ScoreDiagRow>(new CommandDefinition(
            @"SELECT d.*, m.LeagueName, m.HomeTeamName, m.AwayTeamName, m.MatchTime,
                     DATEDIFF(minute, SYSDATETIME(), m.MatchTime) AS MinutesToKick
              FROM dbo.FbScoreDiag d
              JOIN dbo.FbMatch m ON m.MatchId = d.MatchId
              WHERE d.MatchId = @matchId",
            new { matchId }, cancellationToken: ct));
    }

    /// <summary>
    /// 净胜球标签的区分度自检：按尾部标签分组看真实的大比分率。
    ///
    /// 这个接口存在的理由和 tags 一样——标签必须有地方被证伪。
    /// 当前 466 场的结果是「没有区分度」（各档 7%~13%，样本 14~28 场），
    /// 所以页面上那句话写的是「记录」不是「信号」。
    /// </summary>
    public async Task<List<ScoreDiagStat>> GetScoreDiagStatsAsync(int days = 90, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ScoreDiagStat>(new CommandDefinition(
            @"SELECT ISNULL(d.TailTag, N'(未判定)') AS Tag,
                     COUNT(*) AS Matches,
                     CAST(AVG(ABS(d.ExpGd - (CAST(m.HomeScore AS int) - CAST(m.AwayScore AS int)))) AS DECIMAL(6,2)) AS Mae,
                     CAST(AVG(CASE WHEN CAST(m.HomeScore AS int) - CAST(m.AwayScore AS int) >= 3 THEN 1.0 ELSE 0 END) AS DECIMAL(5,3)) AS Pos3Rate,
                     CAST(AVG(CASE WHEN CAST(m.HomeScore AS int) - CAST(m.AwayScore AS int) <= -3 THEN 1.0 ELSE 0 END) AS DECIMAL(5,3)) AS Neg3Rate
              FROM dbo.FbScoreDiag d
              JOIN dbo.FbMatch m ON m.MatchId = d.MatchId
              WHERE m.HomeScore IS NOT NULL AND m.Status = 3
                AND m.MatchTime >= DATEADD(day, -1 * @days, CAST(GETDATE() AS date))
              GROUP BY ISNULL(d.TailTag, N'(未判定)')
              ORDER BY COUNT(*) DESC",
            new { days }, cancellationToken: ct));
        return rows.ToList();
    }
}

/// <summary>
/// 净胜球诊断（FbScoreDiag 一行）。
///
/// 这些数字是「市场怎么定价这场比赛的比分」，不是「我们认为会打成几比几」。
/// 466 场回测：ExpGd 与实际净胜球的 MAE 是 1.32 球，四舍五入全对 24.7%。
/// </summary>
public sealed class ScoreDiagRow
{
    public long MatchId { get; set; }
    public string? MatchNo { get; set; }
    public int Rounds { get; set; }
    public DateTime? FirstAt { get; set; }
    public DateTime? LastAt { get; set; }

    /// <summary>封盘（最后一次快照）的期望净胜球，正数=主队赢。</summary>
    public decimal? ExpGd { get; set; }
    public decimal? ExpGdOpen { get; set; }
    /// <summary>从开盘到封盘，期望净胜球移动了多少球。</summary>
    public decimal? ExpGdMove { get; set; }
    public decimal? ExpTotal { get; set; }

    public decimal? PHome { get; set; }
    public decimal? PDraw { get; set; }
    public decimal? PAway { get; set; }
    public decimal? PHomeOpen { get; set; }

    /// <summary>净胜 ≥3 球的概率（主队大胜）。</summary>
    public decimal? P3Plus { get; set; }
    /// <summary>净负 ≥3 球的概率（客队大胜）。</summary>
    public decimal? PMinus3 { get; set; }
    public decimal? P3PlusMove { get; set; }
    public decimal? PMinus3Move { get; set; }

    /// <summary>客队向深档（净负 ≥3）里，赔率下降 ≥5% 的档数。3 档以上才算「整批」。</summary>
    public int DeepNegDrop { get; set; }
    /// <summary>主队向深档（净胜 ≥3）里，赔率下降 ≥5% 的档数。</summary>
    public int DeepPosDrop { get; set; }

    public string? TailTag { get; set; }
    public string? DistText { get; set; }

    // 以下来自 FbMatch 联表
    public string? LeagueName { get; set; }
    public string? HomeTeamName { get; set; }
    public string? AwayTeamName { get; set; }
    public DateTime? MatchTime { get; set; }
    public int? MinutesToKick { get; set; }

    /// <summary>把 DistText（"−4:14,−3:50" 千分比）拆成前端可直接画的分布。</summary>
    public List<GdProb> Dist
    {
        get
        {
            var list = new List<GdProb>();
            if (string.IsNullOrWhiteSpace(DistText)) return list;
            foreach (var part in DistText.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = part.IndexOf(':');
                if (i <= 0) continue;
                if (!int.TryParse(part[..i], out var gd)) continue;
                if (!int.TryParse(part[(i + 1)..], out var permille)) continue;
                list.Add(new GdProb { Gd = gd, Pct = Math.Round(permille / 10m, 1) });
            }
            return list;
        }
    }
}

public sealed class GdProb
{
    public int Gd { get; set; }
    public decimal Pct { get; set; }
}

public sealed class ScoreDiagStat
{
    public string Tag { get; set; } = "";
    public int Matches { get; set; }
    public decimal? Mae { get; set; }
    public decimal? Pos3Rate { get; set; }
    public decimal? Neg3Rate { get; set; }
}

public sealed class ScanLogResult
{
    public DateOnly Day { get; set; }
    public string Source { get; set; } = "";
    public int Rounds { get; set; }
    public int OkRounds { get; set; }
    public int ErrorRounds { get; set; }
    public DateTime? FirstAt { get; set; }
    public DateTime? LastAt { get; set; }
    /// <summary>相邻两轮间隔未超阈值的累计分钟数，近似「实际在监控的时长」。</summary>
    public int CoveredMinutes { get; set; }
    public int GapThresholdMinutes { get; set; }
    public List<ScanGap> Gaps { get; set; } = new();
    public List<OddsScanRow> Rows { get; set; } = new();
}

public sealed class ScanGap
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int Minutes { get; set; }
}

public sealed class OddsScanRow
{
    public long ScanId { get; set; }
    public string Source { get; set; } = "";
    public DateTime CapturedAt { get; set; }
    public int MatchCount { get; set; }
    public int OptionCount { get; set; }
    public int MoveCount { get; set; }
    public int? ElapsedMs { get; set; }
    public string Status { get; set; } = "";
    public string? Note { get; set; }
    /// <summary>与上一轮的间隔分钟数，第一轮为 null。由 <see cref="OddsCaptureRepository"/> 计算，不落库。</summary>
    public int? GapMinutes { get; set; }
}

public sealed class MatchTrendRow
{
    public long MatchId { get; set; }
    public string PlayType { get; set; } = "";
    public string? MatchNo { get; set; }
    public DateTime FirstAt { get; set; }
    public DateTime LastAt { get; set; }
    public int Rounds { get; set; }
    public int Options { get; set; }
    public int Moves { get; set; }
    public int BigMoves { get; set; }
    public int UpCount { get; set; }
    public int DownCount { get; set; }
    public string? FirstTopCode { get; set; }
    public string? FirstTopName { get; set; }
    public decimal? FirstTopOdds { get; set; }
    public string? LastTopCode { get; set; }
    public string? LastTopName { get; set; }
    public decimal? LastTopOdds { get; set; }
    public decimal? TopShift { get; set; }
    public decimal? OpenWin { get; set; }
    public decimal? OpenDraw { get; set; }
    public decimal? OpenLoss { get; set; }
    public decimal? CurrWin { get; set; }
    public decimal? CurrDraw { get; set; }
    public decimal? CurrLoss { get; set; }
    public DateTime? FirstMoveAt { get; set; }
    public DateTime? LastMoveAt { get; set; }
    public int LateMoves { get; set; }
    public string? TrendTag { get; set; }
    public DateTime UpdatedAt { get; set; }

    // 以下来自 FbMatch 联表，不落 FbMatchTrend
    public string? LeagueName { get; set; }
    public string? HomeTeamName { get; set; }
    public string? AwayTeamName { get; set; }
    public DateTime? MatchTime { get; set; }
    public int? MinutesToKick { get; set; }
    public byte? HomeScore { get; set; }
    public byte? AwayScore { get; set; }

    /// <summary>
    /// 亚盘（FbOdds.OddsType='AH' 平均值公司）让球数。
    /// 500 的符号约定：负 = 主队让球，正 = 客队让球。
    /// 竞彩让球胜平负（NSPF）的官方让球线库里没有，页面上展示让球数一律以亚盘为准。
    /// </summary>
    public decimal? AhOpen { get; set; }

    /// <summary>亚盘终盘让球数。同 AhOpen 的符号约定。</summary>
    public decimal? AhLast { get; set; }

    /// <summary>FbMatch.Status：3 = 已完赛。其他值含义见采集侧定义。</summary>
    public byte? MatchStatus { get; set; }

    /// <summary>胜平负赛果 3/1/0。只对 PlayType=SPF 有意义。</summary>
    public string? ResultSpf { get; set; }

    /// <summary>
    /// 标签方向是否与赛果一致。null = 未开赛/未完赛/非 SPF/标签无法映射。
    ///
    /// 注意这不是「预测准不准」，是「赔率往哪边动」和「最后哪边赢」对不对得上。
    /// 标签本来就可能错——它只是把市场迁移记下来，所以这个字段必须跟着展示出来，
    /// 不然页面上永远看不出标签到底靠不靠谱。
    /// </summary>
    public bool? TagHit { get; set; }
}

public sealed class MatchTrendDetail
{
    public long MatchId { get; set; }
    public string? MatchNo { get; set; }
    public string? LeagueName { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }
    public int? MinutesToKick { get; set; }
    public List<MatchTrendRow> Plays { get; set; } = new();
    public List<OddsMoveRow> Moves { get; set; } = new();
}

public sealed class TrendTagStat
{
    public string Tag { get; set; } = "";
    public int Plays { get; set; }
    public int Matches { get; set; }
    public decimal AvgMoves { get; set; }

    /// <summary>其中有赛果、能判定的场次。HitRate 的分母。</summary>
    public int Settled { get; set; }

    /// <summary>标签方向命中赛果的比例。Settled 太小时不要读它。</summary>
    public decimal? HitRate { get; set; }
}
