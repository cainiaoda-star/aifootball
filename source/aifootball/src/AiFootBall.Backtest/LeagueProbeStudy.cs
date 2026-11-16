using AiFootBall.Core.Models;
using AiFootBall.Data;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 单联赛「能不能套现有模型」的专项诊断（2026-11-09 加，用户问「欧国联能不能推」）。
///
/// 与主回测台的区别：主回测台回答「模型全体上比市场差多少」，这里回答一个更窄、
/// 更可执行的问题——**某个联赛的场次，落在探针自己的扫描窗口里有几注、准不准。**
/// 窗口里的赔率 4～15 只属于这次扫描，不是现网半全场出票闸。现网不再卡半全场赔率区间。
///
/// 为什么必须单独做：欧国联这类国家队赛事在库里的形态和联赛完全不同，
/// ① 半全场联赛分层基线表里根本没有它（只有球会友谊/友谊赛）；
/// ② 每支队一年只踢几场，模型样本极薄；
/// ③ 竞彩只在国际比赛日开几场，赔率快照也就那么几次。
/// 这三点任何一条不成立，都不能拿「模型在五大联赛的表现」外推。
///
/// 口径（刻意与线上一致）：
///   · 纯模型概率 = 两段独立泊松（半场 λ×0.45、下半场 λ×0.55）的 9 档联合，与 HalfFullJoint 同公式；
///   · 市场隐含 = 9 档赔率倒数和归一（去水），赔率取赛前快照 MAX，不用收盘价；
///   · 训练集严格早于测试期，防泄漏。
///
/// 输出三块，缺一不可：
///   一、数据覆盖（有多少场能进模型）
///   二、能不能过门槛（认识的队、样本场次）
///   三、真打起来怎么样（Top-1 命中、校准、按探针扫描窗口的注数与回报）
///
/// 用法：dotnet run --project src/AiFootBall.Backtest -c Release -- league-probe 欧国联 [测试起始日]
/// </summary>
public static class LeagueProbeStudy
{
    private const double HalfShare = 0.45;
    private const int MaxHalfGoals = 6;

    /// <summary>探针自己的概率下限。赔率窗口写在扫描处，不再借引擎常量。</summary>
    private const double MinHalfFullPct = 12.0;

    /// <summary>9 档顺序：先半场结果（3胜/1平/0负），再全场结果。与 MilkCombos 一致。</summary>
    private static readonly string[] Combos = { "3-3", "3-1", "3-0", "1-3", "1-1", "1-0", "0-3", "0-1", "0-0" };

    private sealed class TrainRow
    {
        public string? LeagueName { get; set; }
        public string HomeTeam { get; set; } = "";
        public string AwayTeam { get; set; } = "";
        public DateTime MatchTime { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
    }

    private sealed class QuoteRow
    {
        public long MatchId { get; set; }
        public string? OptionCode { get; set; }
        public decimal Odds { get; set; }
        public string? LeagueName { get; set; }
        public string HomeTeam { get; set; } = "";
        public string AwayTeam { get; set; } = "";
        public DateTime MatchTime { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
        public int? HalfHomeScore { get; set; }
        public int? HalfAwayScore { get; set; }
        public decimal? TotalLine { get; set; }
    }

    private sealed record Pick(string Code, decimal Odds, double ModelPct, double MarketPct, bool Win);

    public static async Task<int> RunAsync(string connStr, string league, DateTime testStart)
    {
        var factory = new SqlConnectionFactory(connStr);

        List<TrainRow> train;
        await using (var c = await factory.OpenAsync())
        {
            train = (await c.QueryAsync<TrainRow>(
                @"SELECT LeagueName, HomeTeamName AS HomeTeam, AwayTeamName AS AwayTeam,
                         MatchTime, HomeScore, AwayScore
                  FROM dbo.FbMatch
                  WHERE Status = 3 AND HomeScore IS NOT NULL AND AwayScore IS NOT NULL
                    AND MatchTime < @testStart",
                new { testStart }, commandTimeout: 300)).ToList();
        }

        if (train.Count < 5000)
        {
            Console.WriteLine($"训练样本只有 {train.Count} 场，不足以拟合。");
            return 1;
        }

        var model = ScorelineModel.Fit(train.Select(t => new ScoreSample
        {
            LeagueName = t.LeagueName,
            HomeTeam = t.HomeTeam,
            AwayTeam = t.AwayTeam,
            HomeScore = t.HomeScore,
            AwayScore = t.AwayScore
        }).ToList());

        if (!model.Ready)
        {
            Console.WriteLine("模型未就绪。");
            return 1;
        }

        Console.WriteLine($"# {league} 专项诊断  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
        Console.WriteLine($"训练样本 {train.Count} 场（严格早于 {testStart:yyyy-MM-dd}），测试期从 {testStart:yyyy-MM-dd} 起。");
        Console.WriteLine();

        // ---------- 一、数据覆盖 ----------
        List<(DateTime MatchTime, string Home, string Away, int Hs, int As, int? Hhs, int? Has, bool HasEuro, int JcBqCount, string League)> matches;
        await using (var c = await factory.OpenAsync())
        {
            matches = (await c.QueryAsync<(DateTime, string, string, int, int, int?, int?, bool, int, string)>(
                @"SELECT m.MatchTime, m.HomeTeamName, m.AwayTeamName, m.HomeScore, m.AwayScore,
                         m.HalfHomeScore, m.HalfAwayScore,
                         CASE WHEN EXISTS (SELECT 1 FROM dbo.FbOdds o WHERE o.MatchId = m.MatchId AND o.OddsType = 'EU') THEN 1 ELSE 0 END,
                         (SELECT COUNT(DISTINCT h.OptionCode) FROM dbo.FbJcOddsHistory h
                          WHERE h.MatchId = m.MatchId AND h.PlayType = 'BQ') AS BqCount,
                         m.LeagueName
                  FROM dbo.FbMatch m
                  WHERE (@league IS NULL OR m.LeagueName = @league) AND m.MatchTime >= @testStart
                    AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                  ORDER BY m.MatchTime",
                new { league = LeagueArg(league), testStart }, commandTimeout: 300)).ToList();
        }

        Console.WriteLine("## 一、测试期数据覆盖");
        Console.WriteLine();
        Console.WriteLine("| 口径 | 场次 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| 完赛场次 | {matches.Count} |");
        Console.WriteLine($"| 有真半场（半全场可判定） | {matches.Count(m => m.Item6 is not null)} |");
        Console.WriteLine($"| 有欧赔 | {matches.Count(m => m.Item8)} |");
        Console.WriteLine($"| 有完整 9 档半全场赔率 | {matches.Count(m => m.Item9 >= 9)} |");
        Console.WriteLine();
        if (matches.Count == 0)
        {
            Console.WriteLine($"测试期（≥ {testStart:yyyy-MM-dd}）没有 {league} 的完赛场次。");
            return 0;
        }

        // ---------- 二、模型认不认识这些队 ----------
        Console.WriteLine("## 二、模型够不够格（线上门槛：两队各 ≥ 3 场，MinTeamGames）");
        Console.WriteLine();
        Console.WriteLine("| 指标 | 值 |");
        Console.WriteLine("|---|---|");
        var gameStats = matches
            .SelectMany(m => new[] { (Lg: m.Item10, Team: m.Item2), (Lg: m.Item10, Team: m.Item3) })
            .Distinct()
            .Select(t => model.TeamGames(t.Lg, t.Team))
            .ToList();
        var known = matches.Count(m => model.KnowsTeam(m.Item10, m.Item2) && model.KnowsTeam(m.Item10, m.Item3));
        var enough = matches.Count(m =>
            model.TeamGames(m.Item10, m.Item2) >= 3 && model.TeamGames(m.Item10, m.Item3) >= 3);
        Console.WriteLine($"| 参赛队数（去重） | {gameStats.Count} |");
        Console.WriteLine($"| 队均本联赛样本 | {(gameStats.Count == 0 ? 0 : gameStats.Average()):F1} 场 |");
        Console.WriteLine($"| 样本中位数 | {(gameStats.Count == 0 ? 0 : Median(gameStats))} 场 |");
        Console.WriteLine($"| 两队都认识 | {known} / {matches.Count} |");
        Console.WriteLine($"| **两队各 ≥3 场（能过线上门槛）** | **{enough} / {matches.Count}** |");
        Console.WriteLine();

        if (enough == 0)
        {
            Console.WriteLine("结论：这个联赛的球队样本全部低于出注门槛，线上不会出任何一注。");
            return 0;
        }

        // ---------- 三、有 9 档赔率的场次：真打 ----------
        Console.WriteLine("## 三、有完整半全场赔率的场次：模型 vs 市场 vs 赛果");
        Console.WriteLine();

        var probeIds = matches.Where(m => m.Item9 >= 9).Select(m => m.Item1).ToHashSet();
        if (probeIds.Count == 0)
        {
            Console.WriteLine("测试期没有完整 9 档赔率，无法做 Top-1 / 校准对比。");
            return 0;
        }

        List<QuoteRow> quotes;
        await using (var c = await factory.OpenAsync())
        {
            quotes = (await c.QueryAsync<QuoteRow>(
                @"WITH q AS (
                      SELECT h.MatchId, h.OptionCode, MAX(h.Odds) AS Odds
                      FROM dbo.FbJcOddsHistory h
                      JOIN dbo.FbMatch m ON m.MatchId = h.MatchId
                      WHERE h.PlayType = 'BQ'
                        AND h.CapturedAt < m.MatchTime
                        AND (@league IS NULL OR m.LeagueName = @league)
                        AND m.MatchTime >= @testStart
                      GROUP BY h.MatchId, h.OptionCode)
                  SELECT q.MatchId, q.OptionCode, q.Odds,
                         m.LeagueName, m.HomeTeamName AS HomeTeam, m.AwayTeamName AS AwayTeam,
                         m.MatchTime, m.HomeScore, m.AwayScore,
                         m.HalfHomeScore, m.HalfAwayScore,
                         ou.TotalLine
                  FROM q
                  JOIN dbo.FbMatch m ON m.MatchId = q.MatchId
                  OUTER APPLY (SELECT TOP 1 TotalLine FROM dbo.FbOdds
                               WHERE MatchId = m.MatchId AND OddsType = 'OU' AND CompanyId = 0
                                 AND IsOpening = 1
                               ORDER BY OddsId DESC) ou",
                new { league = LeagueArg(league), testStart }, commandTimeout: 300)).ToList();
        }

        var byMatch = quotes.GroupBy(q => q.MatchId).ToList();
        var picks = new List<Pick>();
        // 闸门扫描用：每场保存「9 档概率 + 9 档赔率 + 真实结果」，事后按不同阈值重放，
        // 免得为了试一个上限把整条管线重跑一遍。
        var gateRows = new List<(double[] Joint, Dictionary<string, decimal> Odds, string Actual)>();
        var calib = new List<(double Pred, int Hit)>();
        int evaluated = 0, modelTopHit = 0, marketTopHit = 0;
        int modelPickedMatches = 0, modelPickedHit = 0;
        double modelPickedReturn = 0;

        foreach (var g in byMatch)
        {
            var head = g.First();
            if (head.HalfHomeScore is not int hh || head.HalfAwayScore is not int ha) continue;
            if (g.Count() < 9) continue;

            var oddsByCode = new Dictionary<string, decimal>(StringComparer.Ordinal);
            double invSum = 0;
            foreach (var q in g)
            {
                if (q.Odds <= 1m) continue;
                oddsByCode[q.OptionCode ?? ""] = q.Odds;
                invSum += 1.0 / (double)q.Odds;
            }
            if (invSum <= 0 || oddsByCode.Count < 9) continue;

            var joint = Joint(head.LeagueName, head.HomeTeam, head.AwayTeam, head.TotalLine, model);
            if (joint is null) continue;

            var actual = Code(hh, ha, head.HomeScore, head.AwayScore);
            var actualIdx = Array.IndexOf(Combos, actual);
            if (actualIdx < 0) continue;

            evaluated++;
            gateRows.Add((joint, oddsByCode, actual));

            // Top-1：模型最高概率档 vs 市场最低赔（=概率最高）档
            var modelTopIdx = Enumerable.Range(0, 9).MaxBy(i => joint[i]);
            if (Combos[modelTopIdx] == actual) modelTopHit++;
            var marketTopCode = oddsByCode.OrderBy(kv => kv.Value).First().Key;
            if (marketTopCode == actual) marketTopHit++;

            // 校准：9 档全部入桶
            for (var i = 0; i < 9; i++)
                calib.Add((joint[i] * 100.0, Combos[i] == actual ? 1 : 0));

            // 探针自己的扫描窗口，不是现网出票闸：概率 ≥12%、赔率 4~15，每场取模型概率最高的一档。
            // 现网半全场不再使用赔率上下限。
            var eligible = Enumerable.Range(0, 9)
                .Where(i => oddsByCode.TryGetValue(Combos[i], out var o)
                            && o >= 4.0m && o <= 15m
                            && joint[i] * 100.0 >= MinHalfFullPct)
                .OrderByDescending(i => joint[i])
                .ToList();

            if (eligible.Count > 0)
            {
                var idx = eligible[0];
                var code = Combos[idx];
                var odds = oddsByCode[code];
                var marketPct = 100.0 / (double)odds / invSum;
                var win = code == actual;
                picks.Add(new Pick(code, odds, joint[idx] * 100.0, marketPct, win));

                // 线上还有一道基线 EV 闸（BQ 要求 ≥1.00 倍）。这里用市场去水概率当基线的下界近似：
                // 模型概率连市场去水都比不过的，基线 EV 一定 < 1，必被出票闸拦掉。
                if (joint[idx] * 100.0 >= marketPct)
                {
                    modelPickedMatches++;
                    if (win) modelPickedHit++;
                    modelPickedReturn += win ? (double)odds : 0.0;
                }
            }
        }

        if (evaluated == 0)
        {
            Console.WriteLine("没有一场能同时满足「9 档齐全 + 有真半场 + 模型能算」，无法评估。");
            return 0;
        }

        Console.WriteLine($"| 口径 | 场次 | Top-1 命中率 |");
        Console.WriteLine("|---|---|---|");
        Console.WriteLine($"| 模型（9 档最高概率） | {evaluated} | {100.0 * modelTopHit / evaluated:F1}% |");
        Console.WriteLine($"| 市场（最低赔率） | {evaluated} | {100.0 * marketTopHit / evaluated:F1}% |");
        Console.WriteLine();

        Console.WriteLine("校准（模型给该档 X%，实际命中频率）—— 国家队比赛样本薄，看偏差方向不看绝对值：");
        Console.WriteLine();
        Console.WriteLine("| 模型概率 | 档次数 | 平均预测 | 实际命中 | 偏差 |");
        Console.WriteLine("|---|---|---|---|---|");
        foreach (var b in new (string L, double Lo, double Hi)[]
                 { ("0-3%", 0, 3), ("3-6%", 3, 6), ("6-10%", 6, 10), ("10-15%", 10, 15), ("15-25%", 15, 25), ("≥25%", 25, 999) })
        {
            var sub = calib.Where(x => x.Pred >= b.Lo && x.Pred < b.Hi).ToList();
            if (sub.Count == 0) { Console.WriteLine($"| {b.L} | 0 | – | – | – |"); continue; }
            var pred = sub.Average(x => x.Pred);
            var act = sub.Average(x => x.Hit) * 100.0;
            Console.WriteLine($"| {b.L} | {sub.Count} | {pred:F1}% | {act:F1}% | {act - pred:+0.0;-0.0}pp |");
        }
        Console.WriteLine();

        // ---------- 四、探针扫描窗口里有几注 ----------
        Console.WriteLine("## 四、探针扫描窗口（概率≥12%、赔率 4~15、每场一注；不是现网出票闸）");
        Console.WriteLine();
        if (picks.Count == 0)
        {
            Console.WriteLine("**没有一场落进探针窗口**：所有档位都在「概率 ≥12% 且赔率 4~15」之外。这不是现网出票结果。");
            return 0;
        }

        var winCount = picks.Count(p => p.Win);
        var ret = picks.Sum(p => p.Win ? (double)p.Odds : 0.0) / picks.Count;
        Console.WriteLine("| 注数 | 命中 | 命中率 | 平均赔率 | 平注回报 | 模型≥市场的注数 |");
        Console.WriteLine("|---|---|---|---|---|---|");
        Console.WriteLine($"| {picks.Count} | {winCount} | {100.0 * winCount / picks.Count:F1}% | {picks.Average(p => (double)p.Odds):F2} | {ret:F3} | {modelPickedMatches} |");
        Console.WriteLine();

        if (modelPickedMatches > 0)
        {
            var mret = modelPickedReturn / modelPickedMatches;
            Console.WriteLine($"其中「模型概率 ≥ 市场去水概率」的 {modelPickedMatches} 注（这批才可能过线上基线闸）：" +
                              $"命中 {modelPickedHit}，命中率 {100.0 * modelPickedHit / modelPickedMatches:F1}%，平注回报 {mret:F3}。");
        }
        else
        {
            Console.WriteLine("**没有一注的模型概率达到市场去水概率** —— 线上基线闸（BQ 要求 ≥1.00 倍）会全部拦掉，实际出注数 = 0。");
        }
        Console.WriteLine();

        Console.WriteLine("逐注明细：");
        Console.WriteLine();
        Console.WriteLine("| 场次 | 档位 | 赔率 | 模型% | 市场% | 模型/市场 | 结果 |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var p in picks.OrderByDescending(p => p.Odds))
        {
            var ratio = p.MarketPct <= 0 ? 0 : p.ModelPct / p.MarketPct;
            Console.WriteLine($"| – | {p.Code} | {p.Odds:F2} | {p.ModelPct:F1}% | {p.MarketPct:F1}% | {ratio:F2} | {(p.Win ? "中" : "未中")} |");
        }
        Console.WriteLine();

        // ---------- 五、探针自己的窗口扫描：赔率上限 × 概率下限 ----------
        // 4 和 15 是这次扫描的端点，不是现网出票闸。现网半全场不再卡赔率区间。
        // 用途只有一个：回答「把探针窗口拧紧能让命中率涨多少，代价是回报还是注数」。
        // 返奖率固定（BQ 0.797）时，降赔率不改长期 EV，只改方差——扫描就是把这句话量化。
        Console.WriteLine("## 五、探针窗口扫描（变动的是本探针的赔率上限和概率下限，不是现网出票闸）");
        Console.WriteLine();
        Console.WriteLine("| 赔率上限 | 概率下限 | 注数 | 命中 | 命中率 | 平均赔率 | 平注回报 |");
        Console.WriteLine("|---|---|---|---|---|---|---|");

        void Scan(decimal maxOdds, double minPct, bool onlyEdge)
        {
            var n = 0; var hit = 0; var oddsSum = 0.0; var retSum = 0.0;
            foreach (var (joint, odds, actual) in gateRows)
            {
                double invSum = 0;
                foreach (var o in odds.Values)
                    if (o > 1m) invSum += 1.0 / (double)o;

                var idx = -1; var best = -1.0;
                for (var i = 0; i < 9; i++)
                {
                    if (!odds.TryGetValue(Combos[i], out var o)) continue;
                    if (o < 4.0m || o > maxOdds) continue;
                    var pct = joint[i] * 100.0;
                    if (pct < minPct) continue;
                    // 线上基线闸（BQ ≥1.00 倍）的等价下界：模型概率比市场去水概率还低，必被拦掉。
                    if (onlyEdge && invSum > 0 && pct < 100.0 / (double)o / invSum) continue;
                    if (joint[i] > best) { best = joint[i]; idx = i; }
                }

                if (idx < 0) continue;
                var code = Combos[idx]; var od = odds[code];
                n++; oddsSum += (double)od;
                if (code == actual) { hit++; retSum += (double)od; }
            }

            Console.WriteLine(n == 0
                ? $"| {maxOdds:F0} | {minPct:F0}% | 0 | – | – | – | – |"
                : $"| {maxOdds:F0} | {minPct:F0}% | {n} | {hit} | {100.0 * hit / n:F1}% | {oddsSum / n:F2} | {retSum / n:F3} |");
        }

        foreach (var maxOdds in new[] { 6m, 8m, 10m, 12m, 15m })
        foreach (var minPct in new[] { 12.0, 15.0, 20.0 })
            Scan(maxOdds, minPct, onlyEdge: false);

        Console.WriteLine();
        Console.WriteLine("只数「模型概率 ≥ 市场去水概率」那批（更接近线上基线闸放行后的真实结果）：");
        Console.WriteLine();
        Console.WriteLine("| 赔率上限 | 概率下限 | 注数 | 命中 | 命中率 | 平均赔率 | 平注回报 |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var maxOdds in new[] { 6m, 8m, 10m, 12m, 15m })
        foreach (var minPct in new[] { 12.0, 15.0, 20.0 })
            Scan(maxOdds, minPct, onlyEdge: true);
        Console.WriteLine();

        Console.WriteLine($"样本判定：可评估 {evaluated} 场 / 探针窗口 {picks.Count} 注；" +
                          $"参考门槛 30 注 —— {(picks.Count < 30 ? "**样本不足，任何命中率都不可信**" : "样本可读")}。");

        return 0;
    }

    /// <summary>联赛参数：ALL / all / * 表示不限联赛（全库体检）。</summary>
    private static string? LeagueArg(string league) =>
        league.Equals("ALL", StringComparison.OrdinalIgnoreCase) || league == "*" ? null : league;

    private static int Median(List<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    private static string Code(int halfHome, int halfAway, int fullHome, int fullAway)
    {
        static char R(int h, int a) => h > a ? '3' : h == a ? '1' : '0';
        return $"{R(halfHome, halfAway)}-{R(fullHome, fullAway)}";
    }

    /// <summary>9 档联合，顺序同 <see cref="Combos"/>。与 MilkPickEngine.HalfFullJoint 同公式。</summary>
    private static double[]? Joint(string? league, string home, string away, decimal? totalLine, ScorelineModel model)
    {
        var eg = model.ExpectedGoals(league, home, away, totalLine);
        if (eg is null) return null;
        var (lh, la) = eg.Value;

        var first = Grid(lh * HalfShare, la * HalfShare);
        var second = Grid(lh * (1 - HalfShare), la * (1 - HalfShare));
        var m1 = first.Sum(x => x.P);
        var m2 = second.Sum(x => x.P);
        if (m1 <= 0 || m2 <= 0) return null;

        var acc = new double[9];
        foreach (var a in first)
        foreach (var b in second)
        {
            var ht = a.H > a.A ? 0 : a.H == a.A ? 1 : 2;
            var fh = a.H + b.H;
            var fa = a.A + b.A;
            var ft = fh > fa ? 0 : fh == fa ? 1 : 2;
            acc[ht * 3 + ft] += a.P * b.P;
        }

        for (var i = 0; i < 9; i++) acc[i] /= m1 * m2;
        return acc;
    }

    private static List<(int H, int A, double P)> Grid(double lh, double la)
    {
        var list = new List<(int, int, double)>((MaxHalfGoals + 1) * (MaxHalfGoals + 1));
        for (var h = 0; h <= MaxHalfGoals; h++)
        for (var a = 0; a <= MaxHalfGoals; a++)
            list.Add((h, a, Poisson(h, lh) * Poisson(a, la)));
        return list;
    }

    private static double Poisson(int k, double lambda)
    {
        if (k < 0) return 0;
        if (lambda < 1e-6) return k == 0 ? 1 : 0;
        var p = Math.Exp(-lambda);
        for (var i = 1; i <= k; i++) p *= lambda / i;
        return p;
    }
}
