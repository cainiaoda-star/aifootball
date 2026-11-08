using AiFootBall.Core.Models;
using AiFootBall.Data;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 「模型纯值 / 市场隐含」这条闸的阈值校准（2026-11-08，用户当场抓出错注之后补的验证）。
///
/// 背景：周日015 热那亚 vs AC米兰 负胜@36 被引擎选成单列，用户问「根据啥」。
/// 查出来是 0.40 那道否决闸判错了对象——拿 Blend 之后的混合值去比联赛基线，
/// 而 Blend 掺的 0.3 正是市场自己的报价。当场加了 <c>CounterHighModelVsMarketMin = 0.70</c>，
/// 但那个 0.70 是拍的，没有回测支撑。这个类就是补这份证据：**比值的分布到底分不分得开赢家和输家**。
///
/// 口径（刻意与线上引擎一致，否则测的不是同一个东西）：
///   · 纯模型概率 = 两段独立泊松（半场 λ×0.45、下半场 λ×0.55）的 9 档联合，
///     与 <c>MilkPickEngine.HalfFullJoint</c> 同公式，**不掺市场**；
///   · 市场隐含 = 该场 9 档实际赔率倒数和归一后再取该档，即去水之后的真实定价；
///   · 赔率取「赛前快照 MAX」（CapturedAt &lt; MatchTime），不用收盘价，避免赛后回填；
///   · 训练集严格早于测试集切点，防泄漏（本回测台的教训，见 Program 头注释）。
///
/// 用法：dotnet run --project src/AiFootBall.Backtest -c Release -- counter-gate [起始日期]
/// </summary>
public static class CounterGateStudy
{
    private const double HalfShare = 0.45;
    private const int MaxHalfGoals = 6;

    /// <summary>单列口径的赔率区间，与 <c>MilkPickEngine.CounterHighMinOdds/MaxOdds</c> 保持一致。</summary>
    private const decimal MinOdds = 30m;
    private const decimal MaxOdds = 40m;

    private sealed class Quote
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
        public int HalfHomeScore { get; set; }
        public int HalfAwayScore { get; set; }
        public decimal? TotalLine { get; set; }
    }

    private sealed class Sample
    {
        public string? LeagueName { get; set; }
        public string HomeTeam { get; set; } = "";
        public string AwayTeam { get; set; } = "";
        public DateTime MatchTime { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
    }

    private sealed record Hit(double Ratio, double PurePct, double MarketPct, decimal Odds, bool Win);

    public static async Task<int> RunAsync(string connStr, DateTime testStart)
    {
        var factory = new SqlConnectionFactory(connStr);

        // 训练样本：严格早于测试期，且必须有比分（Fit 只吃赛果）。
        List<Sample> train;
        await using (var c = await factory.OpenAsync())
        {
            train = (await c.QueryAsync<Sample>(
                @"SELECT LeagueName, HomeTeamName AS HomeTeam, AwayTeamName AS AwayTeam,
                         MatchTime, HomeScore, AwayScore
                  FROM dbo.FbMatch
                  WHERE Status = 3 AND HomeScore IS NOT NULL AND AwayScore IS NOT NULL
                    AND MatchTime < @testStart",
                new { testStart })).ToList();
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

        Console.WriteLine($"训练样本 {train.Count} 场（< {testStart:yyyy-MM-dd}），测试期从 {testStart:yyyy-MM-dd} 起。");
        Console.WriteLine();

        List<Quote> quotes;
        await using (var c = await factory.OpenAsync())
        {
            quotes = (await c.QueryAsync<Quote>(
                @"WITH q AS (
                      SELECT h.MatchId, h.OptionCode, MAX(h.Odds) AS Odds
                      FROM dbo.FbJcOddsHistory h
                      JOIN dbo.FbMatch m ON m.MatchId = h.MatchId
                      WHERE h.PlayType = 'BQ' AND m.Status = 3
                        AND h.CapturedAt < m.MatchTime
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
                new { testStart }, commandTimeout: 300)).ToList();
        }

        if (quotes.Count == 0)
        {
            Console.WriteLine("测试期没有 BQ 赔率快照。");
            return 1;
        }

        // 按场聚合 9 档
        var byMatch = quotes.GroupBy(q => q.MatchId).ToList();
        var hits = new List<Hit>();
        var marketNine = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var g in byMatch)
        {
            var head = g.First();
            if (g.Count() < 9) continue; // 缺档无法算去水，与引擎 SnapshotComplete 同口径

            marketNine.Clear();
            double invSum = 0;
            foreach (var q in g)
            {
                if (q.Odds <= 1m) continue;
                marketNine[q.OptionCode ?? ""] = q.Odds;
                invSum += 1.0 / (double)q.Odds;
            }
            if (invSum <= 0 || marketNine.Count < 9) continue;

            var eg = model.ExpectedGoals(head.LeagueName, head.HomeTeam, head.AwayTeam, head.TotalLine);
            if (eg is null) continue;

            var joint = Joint(head.LeagueName, head.HomeTeam, head.AwayTeam, head.TotalLine, model);
            if (joint is null) continue;

            foreach (var code in new[] { "0-3", "3-0" })
            {
                if (!marketNine.TryGetValue(code, out var odds)) continue;
                if (odds < MinOdds || odds >= MaxOdds) continue;

                // 市场去水隐含（百分数）= 该档原始概率 / 九档概率和
                var marketPct = 100.0 / (double)odds / invSum;

                var idx = Array.IndexOf(MilkCombos, code);
                if (idx < 0) continue;
                var purePct = joint[idx] * 100.0;

                var win = code == "0-3"
                    ? head.HalfHomeScore < head.HalfAwayScore && head.HomeScore > head.AwayScore
                    : head.HalfHomeScore > head.HalfAwayScore && head.HomeScore < head.AwayScore;

                hits.Add(new Hit(purePct / marketPct, purePct, marketPct, odds, win));
            }
        }

        if (hits.Count == 0)
        {
            Console.WriteLine("测试期没有落在 30~<40 档的反转报价。");
            return 1;
        }

        Console.WriteLine($"样本：{hits.Count} 注（{byMatch.Count} 场里有 9 档完整快照的场次，取 30~<40 倍反转档）");
        Console.WriteLine();

        // 分箱：这是本回测唯一的产出，用来判断 0.70 该落在哪
        var bins = new (string Label, double Lo, double Hi)[]
        {
            ("比值 < 0.50", 0, 0.50),
            ("0.50 ~ 0.60", 0.50, 0.60),
            ("0.60 ~ 0.70", 0.60, 0.70),
            ("0.70 ~ 0.80", 0.70, 0.80),
            ("0.80 ~ 0.90", 0.80, 0.90),
            ("比值 ≥ 0.90", 0.90, double.MaxValue)
        };

        Console.WriteLine("| 模型/市场比值 | 注数 | 命中 | 命中率 | 平均赔率 | 平注回报 |");
        Console.WriteLine("|---|---|---|---|---|---|");
        foreach (var b in bins)
        {
            var sub = hits.Where(h => h.Ratio >= b.Lo && h.Ratio < b.Hi).ToList();
            if (sub.Count == 0)
            {
                Console.WriteLine($"| {b.Label} | 0 | – | – | – | – |");
                continue;
            }
            var win = sub.Count(h => h.Win);
            var avgOdds = sub.Average(h => (double)h.Odds);
            var ret = sub.Sum(h => h.Win ? (double)h.Odds : 0.0) / sub.Count;
            Console.WriteLine($"| {b.Label} | {sub.Count} | {win} | {100.0 * win / sub.Count:F2}% | {avgOdds:F1} | {ret:F3} |");
        }

        Console.WriteLine();
        Console.WriteLine("| 分界 | 注数 | 命中率 | 平注回报 | 平均赔率 |");
        Console.WriteLine("|---|---|---|---|---|");

        void Split(string label, Func<Hit, bool> sel)
        {
            var sub = hits.Where(sel).ToList();
            if (sub.Count == 0) { Console.WriteLine($"| {label} | 0 | – | – | – |"); return; }
            var win = sub.Count(h => h.Win);
            var ret = sub.Sum(h => h.Win ? (double)h.Odds : 0.0) / sub.Count;
            Console.WriteLine($"| {label} | {sub.Count} | {100.0 * win / sub.Count:F2}% | {ret:F3} | {sub.Average(h => (double)h.Odds):F1} |");
        }

        // 0.70 是现行阈值；0.55 是热那亚那条注的实际落点
        Split("比值 < 0.55（热那亚落此）", h => h.Ratio < 0.55);
        Split("0.55 ~ 0.70", h => h.Ratio >= 0.55 && h.Ratio < 0.70);
        Split("≥ 0.70（现行放行）", h => h.Ratio >= 0.70);
        Console.WriteLine();

        // 模型口径：纯模型概率本身值多少
        Console.WriteLine("| 纯模型概率 | 注数 | 命中率 | 平注回报 |");
        Console.WriteLine("|---|---|---|---|");
        foreach (var b in new (string L, double Lo, double Hi)[] { ("< 1.5%", 0, 1.5), ("1.5 ~ 2.0%", 1.5, 2.0), ("2.0 ~ 2.5%", 2.0, 2.5), ("≥ 2.5%", 2.5, 999) })
        {
            var sub = hits.Where(h => h.PurePct >= b.Lo && h.PurePct < b.Hi).ToList();
            if (sub.Count == 0) { Console.WriteLine($"| {b.L} | 0 | – | – |"); continue; }
            var win = sub.Count(h => h.Win);
            var ret = sub.Sum(h => h.Win ? (double)h.Odds : 0.0) / sub.Count;
            Console.WriteLine($"| {b.L} | {sub.Count} | {100.0 * win / sub.Count:F2}% | {ret:F3} |");
        }

        return 0;
    }

    private static readonly string[] MilkCombos =
    {
        "3-3", "3-1", "3-0",
        "1-3", "1-1", "1-0",
        "0-3", "0-1", "0-0"
    };

    /// <summary>9 档联合，顺序同 <see cref="MilkCombos"/>。与 MilkPickEngine.HalfFullJoint 同公式。</summary>
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
