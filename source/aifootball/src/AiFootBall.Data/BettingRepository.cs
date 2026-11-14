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

    /// <summary>
    /// 独立库检：这些写法各自在近 N 个月里踢过多少场（2026-11-14 加）。
    ///
    /// 用途只有一个——判断 `name-unresolved` 到底是「真新队」还是「同一个队换了个写法」。
    /// 判据不能取自引擎：引擎说「我不认识」正是被质疑的那句话。这里只查库：
    /// 按**核心名**（去掉 RB/FC 这类 ASCII 标记）把同一支队的历史全捞进来，再按核心名归并计数。
    /// 归并放在 C#（<see cref="TeamNames.CoreKey"/>）而不是 SQL：SQL 侧没有这份归一规则，
    /// 在 SQL 里再写一份 LIKE 逻辑就是「同一口径两处实现」，迟早对不上。
    /// </summary>
    public async Task<Dictionary<string, int>> GetRecentTeamGamesAsync(
        IEnumerable<string> names, int months = MilkThresholds.TeamHistoryMonths, CancellationToken ct = default)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var queries = (names ?? Array.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => (Name: n!.Trim(), Core: TeamNames.CoreKey(n)))
            .Where(x => x.Core.Length >= 2)
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        if (queries.Count == 0) return result;

        // 同一支队可能同时以两种写法出现在一轮里（主队「大宫松鼠RB」、客队「大宫松鼠」）：
        // 核心名相同 → 只查一次库，但**两个写法都要有答案**，否则第二种写法会被判成「查不到」。
        var byCore = queries.GroupBy(x => x.Core, StringComparer.Ordinal).ToList();

        await using var conn = await _factory.OpenAsync(ct);
        foreach (var grp in byCore)
        {
            var pat = TeamNames.ContainsPattern(grp.Key);
            var rows = (await conn.QueryAsync<(string Name, int N)>(new CommandDefinition(
                @"SELECT name, COUNT(*) AS N FROM (
                      SELECT HomeTeamName AS name FROM dbo.FbMatch
                       WHERE Status = 3 AND MatchTime >= DATEADD(month, -@months, SYSDATETIME())
                         AND HomeTeamName LIKE @pat
                      UNION ALL
                      SELECT AwayTeamName AS name FROM dbo.FbMatch
                       WHERE Status = 3 AND MatchTime >= DATEADD(month, -@months, SYSDATETIME())
                         AND AwayTeamName LIKE @pat
                  ) t
                  GROUP BY name",
                new { months, pat }, cancellationToken: ct))).ToList();

            // ★ 身份闸（2026-11-14 第九轮，第十轮收敛为**与解析同一个函数**）：
            // 核心名相同**不等于**同一支队。解析层已经按梯队身份拒绝 `不来梅 → JFV不来梅U19`、
            // `谢周三 → 谢周三U21`；计数层若只按核心名归并，就会把梯队的场次算进一线队——
            // 同一轮里「解析说不认识」与「库里有 22 场」同时成立，`name-unresolved` 也就永远判不出真假。
            //
            // 第九轮这里用的是另一个「允许互相包含」的变体，第十轮取消：库里存在
            // `BKMA` / `BKMA II`、`WSG蒂洛尔` / `WSG蒂洛尔 II` 这种只差一个 ASCII 记号的两支队，
            // 互相包含会把预备队并进来。现在两边都调 `TeamNames.SameIdentity`——
            // 一套判据、一个方向，计数回答的就是「这些行会不会被解析成本场这支球队」。
            foreach (var q in grp)
                result[q.Name] = rows
                    .Where(r => string.Equals(TeamNames.CoreKey(r.Name), grp.Key, StringComparison.Ordinal)
                                && TeamNames.SameIdentity(q.Name, r.Name))
                    .Sum(r => r.N);
        }

        return result;
    }

    /// <summary>
    /// 源侧快照（2026-11-14 第七轮加）：本轮开始时**在售卡里有哪些场、各自的赔率采到哪一刻、有哪些档**。
    ///
    /// 存在的理由（用户 11-14 明确要求）：发布前的「预期场次」不能取自引擎自己写的输入凭证
    /// （`FbMilkInput`）。引擎在写凭证之前就跳过的场次，两边同时为 0，核对照样通过——
    /// 11-14 的 1419319 就是这样消失的。所以预期必须来自源侧两张表：
    ///   · `BettingCard`（本批受注场次，按 `MAX(CapturedAt)` 取当前批）；
    ///   · `FbJcOddsHistory`（每档最新采样时刻 + 档位清单）。
    ///
    /// 口径与 `JcOddsRepository.GetLatestAsync` **逐字一致**（每键取最新、`Odds > 1`），
    /// 否则「源侧有 31 档、引擎拿到 30 档」会被当成两次不同的采样而误报。
    ///
    /// ★ `asOf` 是本轮 `FbMilkRun.StartedAt`（2026-11-14 第八轮，用户要求）：
    /// 两张表都只取 `CapturedAt &lt;= asOf` 的行。没有这个条件时，事后（或下一轮）追加的采样会
    /// 悄无声息地补进上一轮的「应有场次」，于是那轮到底该有几场永远无法复现——
    /// 11-14 事后回查能看到 11 场、10:58 的赔率，正是这个原因，它证明不了 08:40 那一轮。
    /// 只有 running 头 + 最新采样时刻是不够的：那两者都不参与过滤。
    ///
    /// 只读，不判断受注规则——「这一期能不能买」是纯逻辑，由调用方用
    /// `RecommendationSelection.JcSaleHours` 判，Data 层不引用 Sources 的口径。
    /// </summary>
    public async Task<List<MilkSourceMatch>> GetJcSourceSnapshotAsync(DateTime asOf, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var cards = (await conn.QueryAsync<BettingCardRecord>(new CommandDefinition(
            @"SELECT c.MatchId, c.MatchNo, c.LeagueName, c.HomeTeam, c.AwayTeam,
                     COALESCE(c.MatchTime, f.MatchTime) AS MatchTime
              FROM dbo.BettingCard c
              LEFT JOIN dbo.FbMatch f ON f.MatchId = c.MatchId
              WHERE c.Board='jc'
                AND c.CapturedAt = (SELECT MAX(CapturedAt) FROM dbo.BettingCard
                                    WHERE Board='jc' AND CapturedAt <= @asOf)
                AND c.MatchId IS NOT NULL",
            new { asOf }, cancellationToken: ct))).ToList();
        if (cards.Count == 0) return new List<MilkSourceMatch>();

        var ids = cards.Select(c => c.MatchId!.Value).Distinct().ToArray();
        var points = (await conn.QueryAsync<(long MatchId, string PlayType, string OptionCode, DateTime At)>(
            new CommandDefinition(
                @"SELECT MatchId, PlayType, OptionCode, MAX(CapturedAt) AS At
                  FROM dbo.FbJcOddsHistory
                  WHERE MatchId IN @ids AND Odds > 1 AND CapturedAt <= @asOf
                  GROUP BY MatchId, PlayType, OptionCode",
                new { ids, asOf }, cancellationToken: ct))).ToList();

        var byMatch = points
            .GroupBy(p => p.MatchId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return cards.Select(c =>
        {
            var own = byMatch.TryGetValue(c.MatchId!.Value, out var list) ? list : new();
            return new MilkSourceMatch
            {
                MatchId = c.MatchId!.Value,
                MatchNo = c.MatchNo,
                LeagueName = c.LeagueName,
                HomeTeam = c.HomeTeam,
                AwayTeam = c.AwayTeam,
                MatchTime = c.MatchTime,
                LastSampleAt = own.Count == 0 ? null : own.Max(x => x.At),
                OptionKeys = own
                    .Select(x => $"{x.PlayType}|{x.OptionCode}")
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList()
            };
        }).ToList();
    }


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
    /// 竞彩受注批次的采集状态（2026-11-13 第三轮）。
    ///
    /// 判「采集完整」只能拿**本批**与该期**已知在售场次**比：`BettingCard` 按
    /// (Board, Expect, MatchNo) MERGE，一次只抓到 8 场时另外 3 行的 `CapturedAt` 停在上一批，
    /// 而 <see cref="GetCardsAsync"/> 只读 `CapturedAt = MAX(...)` 那一批 ——
    /// 也就是说**部分采集会让工作集凭空缩小**。若不识别，按不完整的工作集重写榜面，
    /// 没采到的那几场的旧票会被当成「本轮没出」撤掉。
    ///
    /// 只统计 `MatchTime &gt; now` 的行：已开赛的场次本来就不该在批里，
    /// 把它算进「已知」会让每晚的重算都被误判成「不完整」。
    /// </summary>
    public async Task<(DateTime? BatchAt, List<MilkIssueState> Issues)> GetJcCaptureStateAsync(
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var cap = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(CapturedAt) FROM dbo.BettingCard WHERE Board='jc'", cancellationToken: ct));
        if (cap is null) return (null, new List<MilkIssueState>());

        var rows = (await conn.QueryAsync<(string Issue, int Batch, int Known)>(new CommandDefinition(
            @"SELECT LEFT(MatchNo, 2) AS Issue,
                     COUNT(DISTINCT CASE WHEN CapturedAt=@cap THEN MatchNo END) AS Batch,
                     COUNT(DISTINCT MatchNo) AS Known
              FROM dbo.BettingCard
              WHERE Board='jc' AND MatchNo IS NOT NULL AND MatchTime > SYSDATETIME()
              GROUP BY LEFT(MatchNo, 2)
              HAVING COUNT(DISTINCT CASE WHEN CapturedAt=@cap THEN MatchNo END) > 0",
            new { cap }, cancellationToken: ct))).ToList();

        var issues = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Issue))
            .Select(r => new MilkIssueState
            {
                Issue = r.Issue,
                BatchMatches = r.Batch,
                KnownMatches = r.Known
            })
            .ToList();

        return (cap, issues);
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

    /// <summary>
    /// 按「受注日」取受注卡，竞彩复盘专用。竞彩受注和出票都在开赛前：
    /// 欧洲场北京时间凌晨开赛（00:00~11:00），当天下午开赛的才是受注日当天。
    /// 所以受注日 D 的窗口取开赛时间 [D 11:00, D+1 11:00)——
    /// 用开赛日取卡会把「周五002~014」全划给周六，复盘里周五只剩一场（2026-11-06 实测踩到）。
    /// 竞彩每场停售都在开赛时刻（日批 21:58/22:58 只是最后一批），11 点切线足够稳。
    /// </summary>
    public async Task<List<BettingCardRecord>> GetCardsBySaleDayAsync(
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
            new { b = board, from = day.Date.AddHours(11), to = day.Date.AddDays(1).AddHours(11) },
            cancellationToken: ct))).ToList();
    }

    /// <summary>按 MatchId 集合取受注卡（同场保留最后一次采集）。北单复盘只写「今日北单推荐」出现过的场次。</summary>
    public async Task<List<BettingCardRecord>> GetCardsByMatchIdsAsync(
        string board, IReadOnlyCollection<long> matchIds, CancellationToken ct = default)
    {
        if (matchIds.Count == 0) return new List<BettingCardRecord>();
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
                  WHERE c.Board = @b AND c.MatchId IN @ids
              )
              SELECT CardId, Board, Expect, MatchNo, MatchId, LeagueName, HomeTeam, AwayTeam,
                     MatchTime, Handicap, SpfHome, SpfDraw, SpfAway, IsSingle, CapturedAt
              FROM cte WHERE rn = 1
              ORDER BY MatchTime, MatchNo",
            new { b = board, ids = matchIds }, cancellationToken: ct))).ToList();
    }

    /// <summary>某受注日的「今日北单推荐」注（FbBdSpfPick）。MatchId → 各注文本（正路/反向 + 方向@赔率 + 中失）。</summary>
    public async Task<Dictionary<long, List<string>>> GetBdPickMarksAsync(
        DateTime day, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _factory.OpenAsync(ct);
            var rows = (await conn.QueryAsync<BdPickMark>(new CommandDefinition(
                @"SELECT MatchId, Kind, SideName, Odds, Handicap, HandicapSideName, HandicapOdds, Hit
                  FROM dbo.FbBdSpfPick
                  WHERE PickDate = @d", 
                new { d = day.Date }, cancellationToken: ct))).ToList();
            return rows
                .GroupBy(r => r.MatchId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(r =>
                    {
                        // SideName 已是让球盘方向（让胜/让平/让负）时不再拼让球后缀，避免「让让胜」。
                        var side = r.SideName ?? "";
                        var hcp = r.Handicap is decimal h && !string.IsNullOrEmpty(r.HandicapSideName)
                                  && !side.Contains("让")
                            ? " / 让" + r.HandicapSideName
                              + (r.HandicapOdds is decimal ho ? "@" + ho.ToString("0.##") : "")
                            : "";
                        return (r.Kind == "reverse" ? "反向" : "正路") + " " + side
                            + (r.Odds is decimal o ? "@" + o.ToString("0.##") : "")
                            + hcp
                            + (r.Hit is null ? "" : r.Hit is true ? "中" : "失");
                    }).ToList());
        }
        catch (Exception) { return new Dictionary<long, List<string>>(); /* 表未建不影响复盘 */ }
    }

    private sealed class BdPickMark
    {
        public long MatchId { get; set; }
        public string Kind { get; set; } = "steady";
        public string? SideName { get; set; }
        public decimal? Odds { get; set; }
        public decimal? Handicap { get; set; }
        public string? HandicapSideName { get; set; }
        public decimal? HandicapOdds { get; set; }
        public bool? Hit { get; set; }
    }

    /// <summary>某受注日的大奶注，复盘标记「已推荐」用。MatchId → 该场各注「选项@赔率」。</summary>
    public async Task<Dictionary<long, List<string>>> GetMilkPickOptionsAsync(
        DateTime day, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = (await conn.QueryAsync<MilkPickMark>(new CommandDefinition(
            @"SELECT MatchId, OptionName, Odds, Hit
              FROM dbo.FbMilkPick
              WHERE PickDate = @d AND MatchId IS NOT NULL",
            new { d = day.Date }, cancellationToken: ct))).ToList();
        return rows
            .GroupBy(r => r.MatchId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => r.OptionName + "@" + r.Odds.ToString("0.##")
                        + (r.Hit is null ? "" : r.Hit is true ? "中" : "失"))
                    .ToList());
    }

    private sealed class MilkPickMark
    {
        public long MatchId { get; set; }
        public string OptionName { get; set; } = "";
        public decimal Odds { get; set; }
        public bool? Hit { get; set; }
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

    /// <summary>
    /// 按 fid 批量取欧赔终盘主/平/客（平均值 CompanyId=0），受注场次没有 SP 时用来判断冷热。
    ///
    /// <para>
    /// 2026-11-11 修：`FbOdds` 是**原地更新**的「当前盘」（MERGE … SET CapturedAt = SYSDATETIME()），
    /// 所以一场开赛之后还在采样，它那一行的「终盘」就被滚球价顶掉了——
    /// 用 MAX(CASE WHEN IsOpening = 0 …) 取回来的是**开赛后的价**，不是收盘价。
    /// 实测当时 11,532 场有终盘行，其中 **8,330 场（72%）的 CapturedAt 晚于开赛时刻**；
    /// inplay-sample 上线后（每 3 分钟刷一次在踢的场次）这个比例只会更高。
    /// </para>
    /// <para>
    /// 现在的口径分三级：
    /// ① 当前行够早（CapturedAt &lt;= MatchTime）→ 直接用，与旧行为一致；
    /// ② 当前行是开赛后才刷的 → 退回 `FbOddsHistory` 里**开赛前最后一个采样点**；
    /// ③ 历史里也没有开赛前样本 → 返回 NULL。
    /// 「不知道」比「拿滚球价冒充收盘价」好：调用方对 null 本来就有兜底，
    /// 而对「一个很像收盘价的滚球价」没有任何兜底。
    /// </para>
    /// <para>
    /// 新鲜度过滤（freshHours）对两个分支都生效，只按各自的 CapturedAt 判——
    /// 否则会出现「当前行因太旧被排除，却从历史里捞回同样旧的那一版」的绕过。
    /// </para>
    /// </summary>
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
                $@"WITH cur AS (
                      SELECT o.MatchId, o.HomeWin, o.Draw, o.AwayWin, o.CapturedAt, m.MatchTime,
                             ROW_NUMBER() OVER (PARTITION BY o.MatchId ORDER BY o.OddsId DESC) AS rn
                      FROM dbo.FbOdds o
                      JOIN dbo.FbMatch m ON m.MatchId = o.MatchId
                      WHERE o.MatchId IN @ids AND o.OddsType = 'EU' AND o.CompanyId = 0
                        AND o.IsOpening = 0{FreshFilter(freshHours, "o.CapturedAt")}
                  ),
                  pre AS (
                      SELECT h.MatchId,
                             ROW_NUMBER() OVER (PARTITION BY h.MatchId ORDER BY h.CapturedAt DESC) AS rn,
                             h.HomeWin, h.Draw, h.AwayWin
                      FROM dbo.FbOddsHistory h
                      JOIN dbo.FbMatch m ON m.MatchId = h.MatchId
                      WHERE h.MatchId IN @ids AND h.OddsType = 'EU' AND h.CompanyId = 0
                        AND h.CapturedAt <= m.MatchTime{FreshFilter(freshHours, "h.CapturedAt")}
                  )
                  SELECT COALESCE(c.MatchId, p.MatchId) AS MatchId,
                         CASE WHEN c.CapturedAt IS NOT NULL AND c.CapturedAt <= c.MatchTime
                              THEN c.HomeWin ELSE p.HomeWin END AS HomeWin,
                         CASE WHEN c.CapturedAt IS NOT NULL AND c.CapturedAt <= c.MatchTime
                              THEN c.Draw ELSE p.Draw END AS Draw,
                         CASE WHEN c.CapturedAt IS NOT NULL AND c.CapturedAt <= c.MatchTime
                              THEN c.AwayWin ELSE p.AwayWin END AS AwayWin
                  FROM (SELECT * FROM cur WHERE rn = 1) c
                  FULL OUTER JOIN (SELECT * FROM pre WHERE rn = 1) p ON p.MatchId = c.MatchId",
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
    ///
    /// <paramref name="asOf"/> 是**拟合时刻**：只取这一刻之前**已经踢完**的比赛（2026-11-13 加）。
    /// 不传时 SQL 自己用 GETDATE() 兜底，所以「未来才开赛却已有比分」的场次永远进不了训练集，
    /// 不依赖调用方守规矩。为什么必须在这层挡（而不是只在 Fit 里挡）：
    /// 库里有 2 场「开赛在未来、Status=3、比分齐全」的脏数据（威冠联 11-14 22:00、萨尔超 11-15 10:00，
    /// 都不在任何盘口、没有竞彩赔率），它们把拟合窗口推到两天之后——
    /// 只要有人拿全量样本跑 A/B，那就是「模型提前知道答案」的入口。
    /// **有比分、没有赔率，不构成可以进训练集的理由**：能不能用只看开赛时间在不在拟合时刻之前。
    /// ★ 2026-11-13 二次收紧（用户令）：判据从「开赛早于 asOf」改成「开赛 + 150 分钟 ≤ asOf」。
    /// 差别只在历史回测：截止 10-14 23:00 拟合时，10-14 22:30 开赛的那场**当时还没踢完**，
    /// 却因为「今天库里有比分」被算进训练集——这是用未来结果训练过去的模型。
    /// 150 分钟与完场判据（`Status==3 || MatchTime<=now-150min`）同源；线上 asOf=now，代价是刚完场的几场。
    /// 这个分钟数只在 <see cref="ScorelineModel.MatchDurationMinutes"/> 定义一次，这里当参数传，别再写第二个字面量。
    ///
    /// ★ 但「开赛 + 150 分钟」只是**完赛代理**，不是「当时确实已完赛」的证据（用户 2026-11-13 指出）。
    /// 为什么不能直接用库里的时间戳：`FbMatch.UpdatedAt` 是**最后一次写入**时间，
    /// 每次重采都会把老场刷成当前批次（实测近 3 个月 42951 行里 38597 行 UpdatedAt 晚于开赛 3 天，89.9%），
    /// 所以它只能证明「此刻已知」，证明不了「当时未知」。用它当门槛只有两种结局：
    /// 收紧（要求 UpdatedAt ≤ asOf）→ 历史样本几乎被剔光；放松（UpdatedAt ≤ asOf 即放行）→
    /// 同一场是否入模取决于抓取批次，等于把刚修掉的「不可复现」再引进来。
    /// 因此这里保留代理口径，并明确承认残余风险：加时/延期/中断、以及抓取作业每小时一次带来的延迟，
    /// 都可能让个别样本在截止时刻其实还没入库。量化这个残余的工具是 `fit-probe` 第八节。
    ///
    /// <c>ORDER BY f.MatchTime, f.MatchId</c> 不是装饰（2026-11-13 加）：当初 <c>FitRho</c>
    /// 按**下标等距**子采样（17.8 万场取 2500 个，步长 71），顺序一变抽中的样本就换，rho 会在网格上跳格
    /// （实测同一份 178177 场三次跑出 0.010 / 0.000 / -0.010）。ρ 现已固定（<see cref="ScorelineModel.DefaultRho"/>），
    /// 但排序键保留：浮点累加、拟合窗口、未来任何抽样都得有确定顺序，否则「同代码同数据出两张榜」会再来一次。
    /// </summary>
    public async Task<List<ScoreSample>> GetScoreSamplesAsync(
        CancellationToken ct = default, DateTime? before = null, DateTime? asOf = null)
    {
        await using var conn = await _factory.OpenAsync(ct);
        // ★ 2026-11-13 修：这里原来**没 SELECT f.MatchTime**，Dapper 映射到 ScoreSample 时
        // MatchTime 恒为 null（它不在结果集里，不是取不到值）。后果不是报错，是静默：
        //   · ScorelineModel.Fit 的窗口戳恒为 "?..?" —— 审计标准要求「版本戳含拟合窗口」永远读不到；
        //   · 衰减的 anchor 也拿 null，就算传了 halfLifeDays 也会被 WeightOf 当成「没有时间」退回等权，
        //     时间衰减形同虚设（当时恰好没启用，所以没炸）；
        //   · 任何 Fit(samples, asOf: x) 走这条 repo，样本会被 475 行的截止日过滤**全部剔光**、直接返回 Empty。
        // 补上这一列会顺带改变概率（多剔掉 2 场未来场次），所以不要再把它说成「零变化」。
        var rows = await conn.QueryAsync<ScoreSample>(new CommandDefinition(
            @"SELECT f.LeagueName, f.HomeTeamName AS HomeTeam, f.AwayTeamName AS AwayTeam,
                     f.MatchTime, f.HomeScore, f.AwayScore
              FROM dbo.FbMatch f
              WHERE f.Status = 3
                AND f.HomeScore IS NOT NULL AND f.AwayScore IS NOT NULL
                AND f.MatchTime >= DATEADD(month, -18, GETDATE())
                AND (@before IS NULL OR f.MatchTime < @before)
                AND (@asOf IS NULL OR f.MatchTime <= DATEADD(minute, -@dur, @asOf))
                AND f.MatchTime <= DATEADD(minute, -@dur, GETDATE())
              ORDER BY f.MatchTime, f.MatchId",
            new { before, asOf, dur = ScorelineModel.MatchDurationMinutes }, cancellationToken: ct));
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

    /// <summary>
    /// 必发盈亏指数（PnlIdx）最新一次采样：Home/Away。
    /// 盈亏指数深负（如客胜 PnlIdxAway ≤ -60）是「钱往那一边跑」的铁证——
    /// 欧战低水热门反向、主胜大热被客队爆冷（AC米兰/勒沃库森/桑德兰三场）都是这个信号先动。
    /// 与 <see cref="GetExchangeRatioAsync"/> 分开，因为它返回的是另一组字段，调用点也只在风控路径。
    /// </summary>
    public async Task<Dictionary<long, (decimal? PnlIdxHome, decimal? PnlIdxAway)>> GetExchangePnlAsync(
        IEnumerable<long> matchIds, CancellationToken ct = default, double? freshHours = null,
        bool asOfKickoff = false)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        var result = new Dictionary<long, (decimal?, decimal?)>();
        foreach (var chunk in Chunk(ids))
        {
            var rows = await conn.QueryAsync<ExchangePnlRow>(new CommandDefinition(
                $@"SELECT MatchId, PnlIdxHome, PnlIdxAway
                  FROM (
                      SELECT MatchId, PnlIdxHome, PnlIdxAway,
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
                result[row.MatchId] = (row.PnlIdxHome, row.PnlIdxAway);
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

    private sealed class ExchangePnlRow
    {
        public long MatchId { get; set; }
        public decimal? PnlIdxHome { get; set; }
        public decimal? PnlIdxAway { get; set; }
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

    /// <summary>
    /// 进球基线：同一批竞彩联赛、近 N 天、已完场的「联赛 × 比分」聚合。
    ///
    /// 复盘里那张「2 球 / 3 球 / 4 球该押哪档」的表需要两列：今天一天 + 长期基线。
    /// 一天只有二十几场，单看它会得出「3 球占 40%」这种次日就失效的结论；
    /// 基线一定要和受注卡同一批联赛，否则会被低级别联赛和友谊赛稀释
    /// （全库 2021 年起含大量友谊赛/低级别杯赛，直接当基线会把 3 球档压低）。
    ///
    /// 完场判定沿用全库口径：比分齐全 + (Status=3 或 开赛已过 150 分钟)，
    /// 不信 Status 单独一个字段——500 源当晚重采会把 3 洗回 0。
    /// </summary>
    public async Task<List<GoalSampleRow>> GetGoalSamplesAsync(int days, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<GoalSampleRow>(new CommandDefinition(
            @"SELECT m.LeagueName,
                     m.HomeScore,
                     m.AwayScore,
                     COUNT(*) AS Matches
              FROM dbo.FbMatch m
              WHERE m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                AND m.MatchTime >= @from AND m.MatchTime < @until
                AND m.LeagueName IN (SELECT DISTINCT LeagueName FROM dbo.BettingCard)
              GROUP BY m.LeagueName, m.HomeScore, m.AwayScore",
            new
            {
                from = DateTime.Today.AddDays(-Math.Abs(days)),
                until = DateTime.Today
            },
            cancellationToken: ct, commandTimeout: 120))).ToList();
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
                @"SELECT MatchId, HomeScore, AwayScore, HalfHomeScore, HalfAwayScore, Status, MatchTime
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
                  WHERE HomeScore IS NOT NULL AND AwayScore IS NOT NULL
                    AND (Status = 3 OR MatchTime <= DATEADD(minute, -150, GETDATE()))
                    AND MatchId IN @ids",
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
