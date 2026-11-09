using AiFootBall.Core.Models;
using AiFootBall.Data;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 稳胆口径回测（2026-11-09 加，用户拍板「开稳胆栏目」后先证后做）。
///
/// 稳胆的定义只有一句话：**每场取竞彩胜平负里市场最看好（去水概率最高）的那一档**。
/// 不掺模型、不掺方向闸、不看必发——因为这个栏目的目标是「经常能中」，
/// 而模型四项全打不过市场（见 DirectionStudy），掺进来只会把命中率往下拉。
///
/// 这个工具要回答四个问题，答不出来就不该上线：
///   1. 按赔率分箱，命中率与回报各是多少？—— 决定赔率上限该卡在几倍。
///   2. 累计阈值（≤1.5 / ≤2.0 / ≤3.0 …）下注数、命中率、回报怎么走？—— 决定「3 倍以内」是否合理。
///   3. 每天会出几注？—— 决定每日注数上限，避免刷屏。
///   4. 排除不推名单（欧国联/友谊赛/青年赛）与澳超之后，还剩多少？—— 决定名单是否复用。
///
/// 口径与线上一致的部分：
///   · 赔率取「开赛前最后一次采样」（CapturedAt &lt; MatchTime 的 MAX），即实际能买到的收盘价；
///   · 只用三档齐全、去水分母落在 1.02~1.80 的场次（分母离谱说明快照残缺）；
///   · 命中 = 该档对应的实际结果。
///
/// 只读，不改任何业务表，不改线上阈值。
/// 用法：dotnet run --project src/AiFootBall.Backtest -c Release -- steady-probe 2026-10-01
/// </summary>
public static class SteadyStudy
{
    private sealed class MRow
    {
        public long MatchId { get; set; }
        public DateTime MatchTime { get; set; }
        public string? LeagueName { get; set; }
        public string Home { get; set; } = "";
        public string Away { get; set; } = "";
        public int HScore { get; set; }
        public int AScore { get; set; }
    }

    private sealed class QRow
    {
        public long MatchId { get; set; }
        public string OptionCode { get; set; } = "";
        public decimal Odds { get; set; }
    }

    /// <summary>一场的稳胆候选：市场最看好哪一档、赔率多少、去水概率多少、实际中没中。</summary>
    private sealed class Case
    {
        public long MatchId { get; init; }
        public string League { get; init; } = "";
        public string Home { get; init; } = "";
        public string Away { get; init; } = "";
        public DateTime MatchTime { get; init; }
        public char Dir { get; init; }
        public decimal Odds { get; init; }
        public decimal MarketPct { get; init; }
        public bool Hit { get; init; }
        public bool Excluded { get; init; }
    }

    private static readonly string[] ThreeWay = { "3", "1", "0" };

    public static async Task<int> RunAsync(string connStr, DateTime testStart, decimal maxOdds)
    {
        var factory = new SqlConnectionFactory(connStr);

        List<MRow> matches;
        await using (var c = await factory.OpenAsync())
        {
            matches = (await c.QueryAsync<MRow>(
                @"SELECT m.MatchId, m.MatchTime, m.LeagueName,
                         m.HomeTeamName AS Home, m.AwayTeamName AS Away,
                         m.HomeScore AS HScore, m.AwayScore AS AScore
                  FROM dbo.FbMatch m
                  WHERE m.MatchTime >= @testStart
                    AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                    AND EXISTS (SELECT 1 FROM dbo.FbJcOddsHistory h
                                WHERE h.MatchId = m.MatchId AND h.PlayType = 'SPF')
                  ORDER BY m.MatchTime",
                new { testStart }, commandTimeout: 600)).ToList();
        }

        if (matches.Count == 0)
        {
            Console.WriteLine("测试期没有同时具备「赛果 + 竞彩胜平负」的场次。");
            return 0;
        }

        var quotes = new Dictionary<long, Dictionary<string, decimal>>();
        foreach (var chunk in Chunk(matches.Select(m => m.MatchId).ToList()))
        {
            await using var c = await factory.OpenAsync();
            var rows = await c.QueryAsync<QRow>(
                @"SELECT MatchId, OptionCode, Odds FROM (
                      SELECT h.MatchId, h.OptionCode, h.Odds,
                             ROW_NUMBER() OVER (PARTITION BY h.MatchId, h.OptionCode
                                                ORDER BY h.CapturedAt DESC) AS rn
                      FROM dbo.FbJcOddsHistory h
                      JOIN dbo.FbMatch m ON m.MatchId = h.MatchId
                      WHERE h.MatchId IN @ids AND h.PlayType = 'SPF'
                        AND h.CapturedAt < m.MatchTime
                  ) t WHERE rn = 1 AND Odds > 1",
                new { ids = chunk }, commandTimeout: 600);

            foreach (var r in rows)
            {
                if (!quotes.TryGetValue(r.MatchId, out var byOpt))
                    quotes[r.MatchId] = byOpt = new(StringComparer.Ordinal);
                byOpt[r.OptionCode] = r.Odds;
            }
        }

        var cases = new List<Case>(matches.Count);
        var skipped = 0;
        foreach (var m in matches)
        {
            if (!quotes.TryGetValue(m.MatchId, out var q)) { skipped++; continue; }
            if (!q.TryGetValue("3", out var oh) || !q.TryGetValue("1", out var od) || !q.TryGetValue("0", out var oa))
            { skipped++; continue; }

            var over = 1m / oh + 1m / od + 1m / oa;
            if (over is < 1.02m or > 1.80m) { skipped++; continue; }

            var ph = 1m / oh / over;
            var pd = 1m / od / over;
            var pa = 1m / oa / over;

            char dir;
            decimal odds, pct;
            if (ph >= pd && ph >= pa) { dir = '3'; odds = oh; pct = ph; }
            else if (pd >= pa) { dir = '1'; odds = od; pct = pd; }
            else { dir = '0'; odds = oa; pct = pa; }

            var actual = m.HScore > m.AScore ? '3' : m.HScore == m.AScore ? '1' : '0';

            cases.Add(new Case
            {
                MatchId = m.MatchId,
                League = m.LeagueName ?? "",
                Home = m.Home,
                Away = m.Away,
                MatchTime = m.MatchTime,
                Dir = dir,
                Odds = odds,
                MarketPct = pct * 100m,
                Hit = dir == actual,
                Excluded = NoPickLeagues.IsExcluded(m.LeagueName) || NoParlayLeagues.IsExcluded(m.LeagueName)
            });
        }

        Console.WriteLine($"# 稳胆口径回测  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
        Console.WriteLine($"测试期从 {testStart:yyyy-MM-dd} 起：完赛且有胜平负快照 **{matches.Count}** 场，" +
                          $"三档齐全可评估 **{cases.Count}** 场（快照残缺跳过 {skipped} 场）。");
        Console.WriteLine($"其中会被「不推名单 + 澳超」挡掉 **{cases.Count(c => c.Excluded)}** 场，" +
                          $"线上实际可出 **{cases.Count(c => !c.Excluded)}** 场。");
        Console.WriteLine();

        var live = cases.Where(c => !c.Excluded).ToList();
        if (live.Count == 0) { Console.WriteLine("没有可出注样本。"); return 0; }

        // ---------- 一、分箱 ----------
        Console.WriteLine("## 一、按「最看好那一档」的赔率分箱（线上可出样本）");
        Console.WriteLine();
        Console.WriteLine("| 赔率区间 | 注数 | 命中 | 命中率 | 市场隐含均值 | 实际−隐含 | 回报 |");
        Console.WriteLine("|---|---|---|---|---|---|---|");

        var bins = new (decimal Lo, decimal Hi, string Name)[]
        {
            (1.00m, 1.30m, "1.00~1.30"),
            (1.30m, 1.50m, "1.30~1.50"),
            (1.50m, 1.75m, "1.50~1.75"),
            (1.75m, 2.00m, "1.75~2.00"),
            (2.00m, 2.30m, "2.00~2.30"),
            (2.30m, 2.60m, "2.30~2.60"),
            (2.60m, 3.00m, "2.60~3.00"),
            (3.00m, 3.50m, "3.00~3.50"),
            (3.50m, 99m, "3.50+")
        };
        foreach (var b in bins)
        {
            var set = live.Where(c => c.Odds >= b.Lo && c.Odds < b.Hi).ToList();
            if (set.Count == 0) { Console.WriteLine($"| {b.Name} | 0 | – | – | – | – | – |"); continue; }
            Line(b.Name, set);
        }
        Console.WriteLine();

        // ---------- 二、累计阈值 ----------
        Console.WriteLine("## 二、赔率上限扫描（「只买 X 倍以内」）");
        Console.WriteLine();
        Console.WriteLine("| 上限 | 注数 | 占比 | 命中率 | 回报 | 与返奖率 0.878 之差 |");
        Console.WriteLine("|---|---|---|---|---|---|");
        foreach (var cap in new[] { 1.30m, 1.50m, 1.75m, 2.00m, 2.30m, 2.60m, 3.00m, 3.50m, 99m })
        {
            var set = live.Where(c => c.Odds < cap).ToList();
            if (set.Count == 0) continue;
            var hits = set.Count(c => c.Hit);
            var roi = set.Where(c => c.Hit).Sum(c => c.Odds) / set.Count;
            Console.WriteLine($"| ≤{cap:0.00} | {set.Count} | {set.Count * 100.0 / live.Count:0.0}% | " +
                              $"{hits * 100.0 / set.Count:0.00}% | {roi:0.000} | {roi - 0.878m:+0.000;-0.000} |");
        }
        Console.WriteLine();

        // ---------- 三、每日注数 ----------
        Console.WriteLine("## 三、每日注数（按上限 {0:0.00} 倍过滤后）", maxOdds);
        Console.WriteLine();
        var perDay = live.Where(c => c.Odds < maxOdds)
                         .GroupBy(c => c.MatchTime.Date)
                         .Select(g => g.Count())
                         .OrderBy(n => n)
                         .ToList();
        if (perDay.Count == 0)
        {
            Console.WriteLine("该上限下一注都不出，上限设得太紧。");
        }
        else
        {
            Console.WriteLine($"比赛日 {perDay.Count} 天，日均 **{perDay.Average():0.0}** 注，" +
                              $"中位 {perDay[perDay.Count / 2]} 注，最多 {perDay[^1]} 注，" +
                              $"零注日 {perDay.Count(n => n == 0)} 天。");
            Console.WriteLine();
            Console.WriteLine("日注数分布：" + string.Join("，", perDay.GroupBy(n => n)
                .OrderBy(g => g.Key).Select(g => $"{g.Key} 注 × {g.Count()} 天")));
        }
        Console.WriteLine();

        // ---------- 五、每日取最低赔率前 N 注 ----------
        // 为什么要有这一节：全量口径日均 11 注，用户不可能每天打 11 注。
        // 「稳胆」真正的形态应该是「每天挑最把稳的几注」，所以必须验证
        // 「按赔率从低到高取前 N」是否真的比随机取更稳，以及 N 取几合适。
        Console.WriteLine("## 五、每日只取「赔率最低的前 N 注」（上限 {0:0.00} 倍）", maxOdds);
        Console.WriteLine();
        Console.WriteLine("| 每日 N | 注数 | 命中率 | 平均赔率 | 回报 | 有注日 |");
        Console.WriteLine("|---|---|---|---|---|---|");
        var byDay = live.Where(c => c.Odds < maxOdds)
                        .GroupBy(c => c.MatchTime.Date)
                        .ToList();
        foreach (var n in new[] { 1, 2, 3, 4, 5, 6, 8 })
        {
            var take = byDay.SelectMany(g => g.OrderBy(c => c.Odds).Take(n)).ToList();
            if (take.Count == 0) continue;
            var hits = take.Count(c => c.Hit);
            var roi = take.Where(c => c.Hit).Sum(c => c.Odds) / take.Count;
            Console.WriteLine($"| {n} | {take.Count} | {hits * 100.0 / take.Count:0.00}% | " +
                              $"{take.Average(c => c.Odds):0.00} | {roi:0.000} | {byDay.Count} 天 |");
        }
        Console.WriteLine();

        // ---------- 六、按方向 ----------
        Console.WriteLine("## 六、按方向拆（上限 {0:0.00} 倍）", maxOdds);
        Console.WriteLine();
        Console.WriteLine("| 方向 | 注数 | 命中率 | 平均赔率 | 回报 |");
        Console.WriteLine("|---|---|---|---|---|");
        foreach (var d in new[] { '3', '1', '0' })
        {
            var set = live.Where(c => c.Odds < maxOdds && c.Dir == d).ToList();
            if (set.Count == 0) { Console.WriteLine($"| {DirName(d)} | 0 | – | – | – |"); continue; }
            var hits = set.Count(c => c.Hit);
            var roi = set.Where(c => c.Hit).Sum(c => c.Odds) / set.Count;
            Console.WriteLine($"| {DirName(d)} | {set.Count} | {hits * 100.0 / set.Count:0.00}% | " +
                              $"{set.Average(c => c.Odds):0.00} | {roi:0.000} |");
        }
        Console.WriteLine();

        // ---------- 五、联赛 Top ----------
        Console.WriteLine("## 七、出注最多的联赛（上限 {0:0.00} 倍，≥20 注）", maxOdds);
        Console.WriteLine();
        Console.WriteLine("| 联赛 | 注数 | 命中率 | 回报 |");
        Console.WriteLine("|---|---|---|---|");
        foreach (var g in live.Where(c => c.Odds < maxOdds)
                              .GroupBy(c => c.League)
                              .Where(g => g.Count() >= 20)
                              .OrderByDescending(g => g.Count())
                              .Take(15))
        {
            var set = g.ToList();
            var hits = set.Count(c => c.Hit);
            var roi = set.Where(c => c.Hit).Sum(c => c.Odds) / set.Count;
            Console.WriteLine($"| {g.Key} | {set.Count} | {hits * 100.0 / set.Count:0.00}% | {roi:0.000} |");
        }
        Console.WriteLine();

        // ---------- 六、对照 ----------
        Console.WriteLine("## 八、对照");
        Console.WriteLine();
        Console.WriteLine($"· 竞彩胜平负返奖率 **0.878** —— 稳胆是「命中导向」，长期回报只能围绕这个数波动，不可能靠选低赔变正。");
        Console.WriteLine($"· 现大奶主榜（九档混合）243 注命中 14.4%、回报 0.893；稳胆命中率高出约 " +
                          $"{(live.Where(c => c.Odds < maxOdds).Count(c => c.Hit) * 100.0 / Math.Max(1, live.Count(c => c.Odds < maxOdds)) - 14.4):0.0} 个百分点。");
        Console.WriteLine("· 两者**必须分开记账**：稳胆提高的是「中奖频率」，主榜才是按 EV 挑价值的那个。");

        return 0;

        void Line(string name, List<Case> set)
        {
            var hits = set.Count(c => c.Hit);
            var hitPct = hits * 100.0 / set.Count;
            var implied = set.Average(c => (double)c.MarketPct);
            var roi = set.Where(c => c.Hit).Sum(c => c.Odds) / set.Count;
            Console.WriteLine($"| {name} | {set.Count} | {hits} | {hitPct:0.00}% | {implied:0.0}% | " +
                              $"{hitPct - implied:+0.0;-0.0} | {roi:0.000} |");
        }
    }

    private static string DirName(char d) => d switch
    {
        '3' => "主胜",
        '1' => "平局",
        _ => "客胜"
    };

    /// <summary>
    /// 用**线上那套引擎**（<see cref="AiFootBall.Sources.SteadyPickEngine"/>）重跑历史，
    /// 检验它和 `steady-probe` 的统计口径是否一致。
    ///
    /// 为什么必须有这一步：本项目已经踩过一次「回测脚本和线上实现各写一份」的坑
    /// （半全场基线、出票闸与撤注闸门槛不同源）。回测说 77.6%，如果线上代码换个地方算赔率或换个排序，
    /// 两边会给出完全不同的注单，而没人会发现——因为「都看起来合理」。
    ///
    /// 这个入口不自己算概率、不自己排序、不自己判名单，全部委托给线上引擎与线上结算函数。
    /// 两边数字对得上，才说明回测结论可以拿来上线。
    /// </summary>
    public static async Task<int> CheckAsync(string connStr, DateTime testStart)
    {
        var factory = new SqlConnectionFactory(connStr);

        List<MRow> matches;
        await using (var c = await factory.OpenAsync())
        {
            matches = (await c.QueryAsync<MRow>(
                @"SELECT m.MatchId, m.MatchTime, m.LeagueName,
                         m.HomeTeamName AS Home, m.AwayTeamName AS Away,
                         m.HomeScore AS HScore, m.AwayScore AS AScore
                  FROM dbo.FbMatch m
                  WHERE m.MatchTime >= @testStart
                    AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                    AND EXISTS (SELECT 1 FROM dbo.FbJcOddsHistory h
                                WHERE h.MatchId = m.MatchId AND h.PlayType = 'SPF')
                  ORDER BY m.MatchTime",
                new { testStart }, commandTimeout: 600)).ToList();
        }

        if (matches.Count == 0) { Console.WriteLine("没有可用场次。"); return 0; }

        var quotes = new Dictionary<long, Dictionary<string, decimal>>();
        foreach (var chunk in Chunk(matches.Select(m => m.MatchId).ToList()))
        {
            await using var c = await factory.OpenAsync();
            var rows = await c.QueryAsync<QRow>(
                @"SELECT MatchId, OptionCode, Odds FROM (
                      SELECT h.MatchId, h.OptionCode, h.Odds,
                             ROW_NUMBER() OVER (PARTITION BY h.MatchId, h.OptionCode
                                                ORDER BY h.CapturedAt DESC) AS rn
                      FROM dbo.FbJcOddsHistory h
                      JOIN dbo.FbMatch m ON m.MatchId = h.MatchId
                      WHERE h.MatchId IN @ids AND h.PlayType = 'SPF'
                        AND h.CapturedAt < m.MatchTime
                  ) t WHERE rn = 1 AND Odds > 1",
                new { ids = chunk }, commandTimeout: 600);

            foreach (var r in rows)
            {
                if (!quotes.TryGetValue(r.MatchId, out var byOpt))
                    quotes[r.MatchId] = byOpt = new(StringComparer.Ordinal);
                byOpt[r.OptionCode] = r.Odds;
            }
        }

        var settled = new List<(SteadyPick Pick, MRow M)>();
        var days = matches.GroupBy(m => m.MatchTime.Date).OrderBy(g => g.Key).ToList();
        var emptyDays = 0;

        foreach (var day in days)
        {
            // 构造与线上同形的输入：recs（场次）+ odds（每场 SPF 三档）。
            // MatchNo 用 MatchId 代替——稳胆只在「同赔率」时才用它做稳定排序，不影响结果。
            var recs = new List<MatchRecommendation>();
            var odds = new Dictionary<long, List<JcOddsHistoryPoint>>();
            foreach (var m in day)
            {
                if (!quotes.TryGetValue(m.MatchId, out var q)) continue;
                recs.Add(new MatchRecommendation
                {
                    Board = Boards.Jc,
                    MatchNo = m.MatchId.ToString(),
                    MatchId = m.MatchId,
                    LeagueName = m.LeagueName,
                    HomeTeam = m.Home,
                    AwayTeam = m.Away,
                    MatchTime = m.MatchTime
                });
                var list = new List<JcOddsHistoryPoint>(3);
                foreach (var code in ThreeWay)
                    if (q.TryGetValue(code, out var o))
                        list.Add(new JcOddsHistoryPoint
                        {
                            MatchId = m.MatchId, MatchNo = m.MatchId.ToString(),
                            PlayType = JcPlayTypes.Spf, OptionCode = code, Odds = o
                        });
                odds[m.MatchId] = list;
            }

            if (recs.Count == 0) continue;

            var built = AiFootBall.Sources.SteadyPickEngine.Build(recs, odds);
            if (built.Picks.Count == 0) { emptyDays++; continue; }

            foreach (var p in built.Picks)
                settled.Add((p, day.First(m => m.MatchId == p.MatchId)));
        }

        Console.WriteLine($"# 稳胆线上引擎复核  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
        Console.WriteLine($"测试期从 {testStart:yyyy-MM-dd} 起，{days.Count} 个比赛日、{matches.Count} 场完赛。");
        Console.WriteLine($"线上引擎出注 **{settled.Count}** 注，零注日 {emptyDays} 天。");
        Console.WriteLine();

        if (settled.Count == 0) { Console.WriteLine("一注都没出，口径可能被改坏了。"); return 1; }

        var hits = settled.Count(s => s.Pick.Hit ?? SteadySettlement.Judge(s.Pick.Side, s.M.HScore, s.M.AScore) == true);
        var oddsSum = settled.Where(s => SteadySettlement.Judge(s.Pick.Side, s.M.HScore, s.M.AScore) == true)
                             .Sum(s => s.Pick.Odds);

        Console.WriteLine("| 指标 | 值 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| 注数 | {settled.Count} |");
        Console.WriteLine($"| 命中 | {hits} |");
        Console.WriteLine($"| 命中率 | {hits * 100.0 / settled.Count:0.00}% |");
        Console.WriteLine($"| 平均赔率 | {settled.Average(s => s.Pick.Odds):0.00} |");
        Console.WriteLine($"| 回报 | {oddsSum / settled.Count:0.000} |");
        Console.WriteLine();

        // 与 steady-probe 的「每日前 3 注」对照：同一批数据、不同实现，数字必须接近。
        Console.WriteLine("对照 `steady-probe` 的「每日赔率最低前 3 注」：98 注 / 77.55% / 0.993。");
        Console.WriteLine("两条路径算出来的注数应与上表基本一致；若差得多，说明引擎与回测口径已经分叉。");
        Console.WriteLine();

        Console.WriteLine("逐日明细（前 10 天）：");
        Console.WriteLine();
        Console.WriteLine("| 日期 | 注数 | 命中 | 赔率 |");
        Console.WriteLine("|---|---|---|---|");
        foreach (var g in settled.GroupBy(s => s.M.MatchTime.Date).OrderBy(g => g.Key).Take(10))
        {
            var h = g.Count(s => SteadySettlement.Judge(s.Pick.Side, s.M.HScore, s.M.AScore) == true);
            Console.WriteLine($"| {g.Key:yyyy-MM-dd} | {g.Count()} | {h} | " +
                              $"{string.Join(" / ", g.Select(s => $"{s.Pick.SideName}@{s.Pick.Odds:0.00}"))} |");
        }

        return 0;
    }

    private static IEnumerable<long[]> Chunk(List<long> ids, int size = 300)
    {
        for (var i = 0; i < ids.Count; i += size)
            yield return ids.Skip(i).Take(size).ToArray();
    }
}
