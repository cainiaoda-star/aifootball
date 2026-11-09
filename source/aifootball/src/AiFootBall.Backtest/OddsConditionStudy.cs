using AiFootBall.Core.Models;
using AiFootBall.Data;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 「赔率档 × 档位」条件化基线到底值不值得建（2026-11-08）。
///
/// 直接起因：江原FC vs 全北现代 半全场负负 @5.30，我先报「真实 EV 0.921」，
/// 那是拿 K1 联赛的平均负负率 17.37% 算的。用户追问后按客胜赔率条件化重算，
/// 同类场次只有 14.4%，EV 掉到 0.77——**差 16 个点，足以改变"下不下"的结论**。
/// 同一个错误此前在比分 0:0 上犯过一次（全库 7.35% 算出 EV 1.139，市场只给 6.4%）。
///
/// 这个类回答三个问题，全部用同一个训测切分（训练 &lt; 2026-05-01，测试 ≥）：
///   1. **条件化有没有让预测更准**：Brier(边际基线) vs Brier(条件基线)。
///      边际基线 = 该档位全局命中率（现在 <c>OptionBaseline</c> 用的就是这个）；
///      条件基线 = 该档位在该赔率箱内的命中率。
///   2. **各赔率箱的真实回报**：命中率 × 平均赔率 = ROI。这是最终裁判，
///      Brier 只是中间指标——预测得准不等于赚钱。
///   3. **边际基线在哪个方向错得最狠**：边际值减去条件值，正数=对这类场次高估。
///
/// 口径：
///   · 赔率取「赛前快照 MAX」（CapturedAt &lt; MatchTime），不用收盘价，避免赛后回填；
///   · 只做 SPF / JQ / BQ，不做 BF——比分 31 档 + 三个"其它"，箱子碎到没法估计，
///     而且它返奖率 0.710 最差，本来就不该主推；
///   · 赔率箱按 log 均匀切 8 段，段边界固定在训练集上算，测试集直接用，防泄漏。
///
/// 用法：dotnet run --project src/AiFootBall.Backtest -c Release -- odds-cond [测试起始日 默认 2026-05-01]
/// </summary>
public static class OddsConditionStudy
{
    private const int Bins = 8;

    private sealed class Quote
    {
        public long MatchId { get; set; }
        public string PlayType { get; set; } = "";
        public string OptionCode { get; set; } = "";
        public decimal Odds { get; set; }
        public DateTime MatchTime { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
        public int? HalfHomeScore { get; set; }
        public int? HalfAwayScore { get; set; }
    }

    public static async Task<int> RunAsync(string connStr, DateTime testStart)
    {
        var factory = new SqlConnectionFactory(connStr);

        Console.WriteLine($"加载 2024-08 起 SPF/JQ/BQ 赛前最高赔率（测试期从 {testStart:yyyy-MM-dd} 起）…");
        List<Quote> all;
        await using (var c = await factory.OpenAsync())
        {
            all = (await c.QueryAsync<Quote>(
                @"WITH q AS (
                      SELECT h.MatchId, h.PlayType, h.OptionCode, MAX(h.Odds) AS Odds
                      FROM dbo.FbJcOddsHistory h
                      JOIN dbo.FbMatch m ON m.MatchId = h.MatchId
                      WHERE h.PlayType IN ('SPF','JQ','BQ')
                        AND m.Status = 3 AND m.HomeScore IS NOT NULL
                        AND h.CapturedAt < m.MatchTime
                        AND m.MatchTime >= '2024-08-01'
                      GROUP BY h.MatchId, h.PlayType, h.OptionCode)
                  SELECT q.MatchId, q.PlayType, q.OptionCode, q.Odds,
                         m.MatchTime, m.HomeScore, m.AwayScore,
                         m.HalfHomeScore, m.HalfAwayScore
                  FROM q JOIN dbo.FbMatch m ON m.MatchId = q.MatchId",
                commandTimeout: 600)).ToList();
        }

        if (all.Count < 10000)
        {
            Console.WriteLine($"只加载到 {all.Count} 条报价，数据不足。");
            return 1;
        }

        // 半场比分缺失的场次不能进 BQ 统计（否则会被当成半场 0:0，负负/胜胜全错）。
        var quotes = all.Where(q => q.PlayType != "BQ" || (q.HalfHomeScore is not null && q.HalfAwayScore is not null)).ToList();
        var train = quotes.Where(q => q.MatchTime < testStart).ToList();
        var test = quotes.Where(q => q.MatchTime >= testStart).ToList();
        Console.WriteLine($"报价 {quotes.Count} 条：训练 {train.Count} / 测试 {test.Count}，覆盖 {quotes.Select(q => q.MatchId).Distinct().Count()} 场。");
        Console.WriteLine();

        // ---- 训练集：算边际基线与各玩法的赔率箱边界 ----
        var marginal = new Dictionary<(string, string), double>();
        var binEdges = new Dictionary<string, decimal[]>();

        foreach (var grp in train.GroupBy(q => q.PlayType))
        {
            var odds = grp.Select(q => (double)q.Odds).OrderBy(x => x).ToList();
            var edges = new decimal[Bins - 1];
            for (var i = 1; i < Bins; i++)
            {
                var pos = (int)Math.Round(odds.Count * i / (double)Bins) - 1;
                edges[i - 1] = (decimal)odds[Math.Clamp(pos, 0, odds.Count - 1)];
            }
            for (var i = 1; i < edges.Length; i++)
                if (edges[i] <= edges[i - 1]) edges[i] = edges[i - 1] + 0.01m;
            binEdges[grp.Key] = edges;
        }

        foreach (var grp in train.GroupBy(q => (q.PlayType, q.OptionCode)))
            marginal[grp.Key] = (double)grp.Count(q => IsHit(q)) / grp.Count();

        var cond = new Dictionary<(string, string, int), double>();
        foreach (var grp in train.GroupBy(q => (q.PlayType, q.OptionCode, BinOf(q, binEdges))))
            cond[grp.Key] = (double)grp.Count(q => IsHit(q)) / grp.Count();

        // ---- 测试集：Brier 对比 ----
        Console.WriteLine($"## 一、条件化是否让预测更准（测试集 Brier，越小越好，基线 0.25）");
        Console.WriteLine();
        Console.WriteLine("| 玩法 | 测试注数 | 边际基线 Brier | 条件基线 Brier | 差 | 赢家 |");
        Console.WriteLine("|---|---|---|---|---|---|");

        double overallM = 0, overallC = 0;
        var overallN = 0;
        foreach (var grp in test.GroupBy(q => q.PlayType))
        {
            double m = 0, cc = 0;
            var n = 0;
            foreach (var q in grp)
            {
                var pM = marginal.TryGetValue((q.PlayType, q.OptionCode), out var pm) ? pm
                    : marginal.Where(kv => kv.Key.Item1 == q.PlayType).Select(kv => kv.Value).DefaultIfEmpty(0).Average();
                var pC = cond.TryGetValue((q.PlayType, q.OptionCode, BinOf(q, binEdges)), out var pc) ? pc : pM;
                var y = IsHit(q) ? 1.0 : 0.0;
                m += (pM - y) * (pM - y);
                cc += (pC - y) * (pC - y);
                n++;
            }
            if (n == 0) continue;
            m /= n; cc /= n;
            overallM += m * n; overallC += cc * n; overallN += n;
            Console.WriteLine($"| {grp.Key} | {n} | {m:F5} | {cc:F5} | {cc - m:+0.00000;-0.00000} | {(cc < m ? "条件化" : "边际")} |");
        }
        if (overallN > 0)
            Console.WriteLine($"| **合计** | **{overallN}** | **{overallM / overallN:F5}** | **{overallC / overallN:F5}** | **{(overallC - overallM) / overallN:+0.00000;-0.00000}** | |");
        Console.WriteLine();

        // ---- 各赔率箱的真实回报 ----
        Console.WriteLine("## 二、各赔率箱的真实回报（命中率 × 平均赔率；1.000 以下是亏）");
        foreach (var play in new[] { "SPF", "JQ", "BQ" })
        {
            var rows = test.Where(q => q.PlayType == play).ToList();
            if (rows.Count == 0) continue;
            Console.WriteLine();
            Console.WriteLine($"### {play}（测试 {rows.Count} 注）");
            Console.WriteLine();
            Console.WriteLine("| 赔率箱 | 注数 | 平均赔率 | 实际命中 | 边际基线 | 条件基线 | ROI |");
            Console.WriteLine("|---|---|---|---|---|---|---|");
            foreach (var g in rows.GroupBy(q => BinOf(q, binEdges)).OrderBy(g => g.Key))
            {
                var n = g.Count();
                var avg = (double)g.Average(q => q.Odds);
                var hit = (double)g.Count(q => IsHit(q)) / n;
                var pM = g.GroupBy(q => q.OptionCode)
                          .Select(x => marginal.TryGetValue((play, x.Key), out var v) ? v : 0)
                          .DefaultIfEmpty(0).Average();
                // 条件基线：同箱内有训练样本的档位取其条件值，没有的记 0（显示为 —）。
                var pC = g.GroupBy(q => q.OptionCode)
                          .Select(x => cond.TryGetValue((play, x.Key, g.Key), out var v2) ? v2 : 0)
                          .DefaultIfEmpty(0).Average();
                Console.WriteLine($"| {BinLabel(binEdges[play], g.Key)} | {n} | {avg:F2} | {hit * 100:F2}% | {pM * 100:F2}% | {(pC > 0 ? $"{pC * 100:F2}%" : "—")} | **{hit * avg:F3}** |");
            }
        }

        // ---- 边际基线的偏差方向 ----
        Console.WriteLine();
        Console.WriteLine("## 三、边际基线在哪个方向错得最狠（边际 − 条件，正数=对这类场次高估）");
        Console.WriteLine();
        Console.WriteLine("| 玩法 | 档位 | 赔率箱 | 边际 | 条件 | 偏差 | 注数 |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        var worst = new List<(string Play, string Code, int Bin, double Marg, double Cond, int N, double Gap)>();
        foreach (var g in train.GroupBy(q => (q.PlayType, q.OptionCode, BinOf(q, binEdges))))
        {
            if (g.Count() < 150) continue;
            if (!marginal.TryGetValue((g.Key.Item1, g.Key.Item2), out var pm)) continue;
            if (!cond.TryGetValue(g.Key, out var pc)) continue;
            worst.Add((g.Key.Item1, g.Key.Item2, g.Key.Item3, pm, pc, g.Count(), pm - pc));
        }
        foreach (var w in worst.OrderByDescending(x => Math.Abs(x.Gap)).Take(12))
            Console.WriteLine($"| {w.Play} | {w.Code} | {BinLabel(binEdges[w.Play], w.Bin)} | {w.Marg * 100:F2}% | {w.Cond * 100:F2}% | **{w.Gap * 100:+0.0;-0.0}** | {w.N} |");

        Console.WriteLine();
        Console.WriteLine("读法提醒：ROI 是唯一裁判。Brier 变好但 ROI 不动 = 只是把误差换了个位置，不产生 alpha。");
        return 0;
    }

    private static int BinOf(Quote q, Dictionary<string, decimal[]> edges)
    {
        if (!edges.TryGetValue(q.PlayType, out var e)) return 0;
        var b = 0;
        while (b < e.Length && q.Odds > e[b]) b++;
        return b;
    }

    private static string BinLabel(decimal[] edges, int bin)
    {
        if (bin == 0) return $"<{edges[0]:0.00}";
        if (bin >= edges.Length) return $"≥{edges[^1]:0.00}";
        return $"{edges[bin - 1]:0.00}-{edges[bin]:0.00}";
    }

    private static bool IsHit(Quote q) => q.PlayType switch
    {
        "SPF" => q.OptionCode == (q.HomeScore > q.AwayScore ? "3" : q.HomeScore == q.AwayScore ? "1" : "0"),
        "JQ" => q.OptionCode == (q.HomeScore + q.AwayScore).ToString(),
        "BQ" => q.HalfHomeScore is int hh && q.HalfAwayScore is int ha
                && q.OptionCode == string.Concat(
                    hh > ha ? '3' : hh == ha ? '1' : '0',
                    "-",
                    q.HomeScore > q.AwayScore ? '3' : q.HomeScore == q.AwayScore ? '1' : '0'),
        _ => false
    };
}
