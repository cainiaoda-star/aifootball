using AiFootBall.Core.Models;
using AiFootBall.Data;
using AiFootBall.Sources;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// ρ（Dixon-Coles 低比分相关修正）的前向对比台（2026-11-13 加，命令 `rho-study`）。只读。
///
/// 起因：11-13 给取样 SQL 加 <c>ORDER BY</c> 之后，同一份 17.8 万场算出的 ρ 从「有时 +0.010、
/// 有时 0.000、有时 −0.010」变成稳定 −0.060。这不是修了个 bug 就完事——**它改变了模型行为**：
/// λh·λa ≈ 1.54 时，0:0 类格子相对 +11%、1:0 类 −7.6%。「可复现」只证明以后每张榜一样，
/// 不证明这个值更准；「落在足坛常见的 −0.03~−0.13」也只是先验，不能当上线依据。
///
/// 这个台子回答三件事，顺序固定、训练集唯一：
///   1. ρ 到底有多不可识别：同一份训练集换个物理顺序，Fit 给出的 ρ 散布多大；
///   2. 固定 λ 只换 ρ，在前向测试集上比分 LogLoss / 胜平负 Brier / 半全场 Brier / 总进球 Brier
///      与低比分档校准差多少（配对差 + 95% 区间，不是只报均值）；
///   3. 全量似然（不抽子样本）重估出来的 ρ 是多少——**另列**，不默认它更好。
///
/// 纪律：测试集完全不参与 ρ 的选择。ρ 的候选值来自三处——旧代码实际落过的值（+0.010 / 0.000 / −0.010，
/// 有历史版本戳为证）、当前值 −0.060、以及只用训练集算出的全量似然最优值。
/// </summary>
public static class RhoStudy
{
    /// <summary>旧代码在不同物理顺序下实际落过、且被版本戳记录在案的值。</summary>
    private static readonly double[] LegacyRhos = { 0.010, 0.000, -0.010 };

    private sealed class Row
    {
        public long MatchId { get; set; }
        public string? LeagueName { get; set; }
        public string HomeTeam { get; set; } = "";
        public string AwayTeam { get; set; } = "";
        public DateTime MatchTime { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
        public int? HalfHomeScore { get; set; }
        public int? HalfAwayScore { get; set; }
    }

    private sealed class Acc
    {
        public double LogLoss;
        public int N;
        public double Brier1x2;
        public double BrierJq;
        public double BrierBq;
        public int NBq;
        public readonly double[] LowPred = new double[6];
        public readonly double[] LowHit = new double[6];
        // 逐场差值（用于配对区间）：相对基准 ρ 的 logloss / 1x2 brier 差
        public readonly List<double> DLl = new();
        public readonly List<double> DBrier = new();
    }

    private static readonly (int H, int A)[] LowCells =
        { (0, 0), (1, 0), (0, 1), (1, 1), (2, 1), (2, 0) };

    public static async Task<int> RunAsync(string connStr)
    {
        var factory = new SqlConnectionFactory(connStr);
        await using var conn = await factory.OpenAsync();

        // 与线上同口径：Status=3、比分齐全、18 个月窗口、开赛 150 分钟以上（截止时确实已完赛）。
        var rows = (await conn.QueryAsync<Row>(@"
SELECT f.MatchId, f.LeagueName, f.HomeTeamName AS HomeTeam, f.AwayTeamName AS AwayTeam,
       f.MatchTime, f.HomeScore, f.AwayScore, f.HalfHomeScore, f.HalfAwayScore
FROM dbo.FbMatch f
WHERE f.Status = 3
  AND f.HomeScore IS NOT NULL AND f.AwayScore IS NOT NULL
  AND f.MatchTime >= DATEADD(month, -18, GETDATE())
  AND f.MatchTime <= DATEADD(minute, -150, GETDATE())
ORDER BY f.MatchTime, f.MatchId")).ToList();

        Console.WriteLine($"# ρ 前向对比（rho-study）  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
        Console.WriteLine($"样本（已完赛、18 个月）：**{rows.Count}** 场，"
            + $"{rows.Min(r => r.MatchTime):yyyy-MM-dd} .. {rows.Max(r => r.MatchTime):yyyy-MM-dd}");
        Console.WriteLine();

        if (rows.Count < 20000)
        {
            Console.WriteLine("!! 样本不足 2 万场，拒绝出结论。");
            return 1;
        }

        // ---- 固定切分：按开赛时间前 70% 训练、后 30% 测试 ----
        var cut = rows[(int)(rows.Count * 0.70)];
        var trainRows = rows.TakeWhile(r => r.MatchTime < cut.MatchTime).ToList();
        var testRows = rows.Skip(trainRows.Count).ToList();
        Console.WriteLine($"切分：训练 **{trainRows.Count}** 场（≤ {trainRows[^1].MatchTime:yyyy-MM-dd HH:mm}），"
            + $"测试 **{testRows.Count}** 场（≥ {testRows[0].MatchTime:yyyy-MM-dd HH:mm}）。");
        Console.WriteLine("测试集不参与任何 ρ 的选择。");
        Console.WriteLine();

        var train = trainRows.Select(ToSample).ToList();
        var baseModel = ScorelineModel.Fit(train);
        if (!baseModel.Ready)
        {
            Console.WriteLine("!! 训练集拟合失败。");
            return 1;
        }

        // ---------- 一、ρ 的可识别性：同一份训练集，换物理顺序 ----------
        Console.WriteLine("## 一、同一份训练集，换物理顺序时 Fit 给出的 ρ");
        Console.WriteLine();
        Console.WriteLine("旧代码的取样 SQL 没有 `ORDER BY`，SQL Server 返回顺序一变，`FitRho` 按步长抽中的");
        Console.WriteLine("2500 个子样本就换了。下表就是那个不稳定性本身。");
        Console.WriteLine();
        Console.WriteLine("| 顺序 | ρ | 场次 |");
        Console.WriteLine("|---|---|---|");
        var orders = new (string Name, List<ScoreSample> Data)[]
        {
            ("按时间正序（现在的 SQL）", train),
            ("按时间倒序", Enumerable.Reverse(train).ToList()),
            ("随机 #1", Shuffle(train, 1)),
            ("随机 #2", Shuffle(train, 2)),
            ("随机 #3", Shuffle(train, 3)),
        };
        var orderRhos = new List<double>();
        foreach (var (name, data) in orders)
        {
            var m = ScorelineModel.Fit(data);
            orderRhos.Add(m.Rho);
            Console.WriteLine($"| {name} | {m.Rho:0.000} | {m.FitGames} |");
        }
        Console.WriteLine();
        Console.WriteLine($"散布：min **{orderRhos.Min():0.000}** / max **{orderRhos.Max():0.000}**，"
            + $"极差 **{orderRhos.Max() - orderRhos.Min():0.000}**。"
            + (orderRhos.Max() - orderRhos.Min() >= 0.02
                ? " → ρ 本身弱识别，单个值（含 −0.060）都不能当成「估准了」。"
                : " → 本次顺序扰动不大，但不构成精度证据。"));
        Console.WriteLine();

        // ---------- 二、全量似然重估（另列方案，不默认更好） ----------
        Console.WriteLine("## 二、全量似然重估（不抽子样本；另列方案）");
        Console.WriteLine();
        var bestRho = double.NaN;
        var bestLl = double.NegativeInfinity;
        for (var rho = -0.20; rho <= 0.0501; rho += 0.01)
        {
            var (ll, _) = baseModel.WithRho(rho).Score(train);
            if (ll > bestLl) { bestLl = ll; bestRho = Math.Round(rho, 3); }
        }
        var (fitLl, fitN) = baseModel.WithRho(baseModel.Rho).Score(train);
        Console.WriteLine($"- 全量最优 ρ = **{bestRho:0.000}**（训练集 {fitN} 场，平均逐场 log-likelihood {bestLl:0.000000}）");
        Console.WriteLine($"- 当前值 ρ = {baseModel.Rho:0.000} 的全量 log-likelihood = {fitLl:0.000000}"
            + $"（差 {bestLl - fitLl:+0.000000;-0.000000}）");
        Console.WriteLine("- 说明：全量似然「更大」不等于「预测更准」——它优化的是训练集拟合度，");
        Console.WriteLine("  过拟合同样会让它变大。是否采用要看你下面第三节的前向成绩，不因为它叫「全量」就更好。");
        Console.WriteLine();

        // ---------- 三、前向评测 ----------
        Console.WriteLine("## 三、前向测试集成绩（固定 λ，只换 ρ）");
        Console.WriteLine();
        var candidates = new List<(string Label, double Rho)>();
        foreach (var r in LegacyRhos) candidates.Add(($"旧代码落过的值 {r:+0.000;-0.000}", r));
        // 线上 11-13 版本戳里的值（全库 178177 场按 count/2500 抽样得到的）。它与训练集上重估的
        // 子采样值不一定相同——这正是「弱识别」的证据，两档必须并排出现在同一张表里。
        candidates.Add(("线上当前值 -0.060（全库子采样）", -0.060));
        candidates.Add(($"训练集子采样重估 {baseModel.Rho:0.000}", baseModel.Rho));
        candidates.Add(($"训练集全量重估 {bestRho:0.000}", bestRho));
        candidates.Add(("ρ = 0（不修正）", 0.0));

        var results = new List<(string Label, double Rho, Acc A)>();
        foreach (var (label, rho) in candidates)
        {
            var acc = Evaluate(baseModel.WithRho(rho), testRows);
            results.Add((label, rho, acc));
        }

        // 以「旧代码落过的值中最高频的 +0.010」为配对基准
        var baseline = results.First(r => Math.Abs(r.Rho - 0.010) < 1e-9).A;

        Console.WriteLine("| ρ | 比分 LogLoss | 胜平负 Brier | 总进球 Brier | 半全场 Brier | 半全场场次 |");
        Console.WriteLine("|---|---|---|---|---|---|");
        foreach (var (label, rho, a) in results)
            Console.WriteLine($"| {label} | {a.LogLoss:0.00000} | {a.Brier1x2:0.00000} | "
                + $"{a.BrierJq:0.00000} | {(a.NBq > 0 ? a.BrierBq.ToString("0.00000") : "—")} | {a.NBq} |");
        Console.WriteLine();
        Console.WriteLine("注：Brier 为多分类 Σ(p−y)² 的逐场均值（不除类数）；比分 LogLoss 按实际比分格取 −log p。");
        Console.WriteLine();

        // 配对差（相对 +0.010）
        Console.WriteLine("### 与「旧代码 +0.010」的配对差（同一批测试场次，95% 区间）");
        Console.WriteLine();
        Console.WriteLine("| ρ | Δ比分 LogLoss | Δ胜平负 Brier |");
        Console.WriteLine("|---|---|---|");
        foreach (var (label, rho, a) in results)
        {
            if (ReferenceEquals(a, baseline)) continue;
            var dl = PairedDiff(a.DLl, baseline.DLl);
            var db = PairedDiff(a.DBrier, baseline.DBrier);
            Console.WriteLine($"| {label} | {dl.Mean:+0.00000;-0.00000} [{dl.Lo:+0.00000;-0.00000}, {dl.Hi:+0.00000;-0.00000}] "
                + $"| {db.Mean:+0.00000;-0.00000} [{db.Lo:+0.00000;-0.00000}, {db.Hi:+0.00000;-0.00000}] |");
        }
        Console.WriteLine();
        Console.WriteLine("区间跨 0 = 这批测试集分不出高低；区间不跨 0 且方向为负 = 那一侧更好。");
        Console.WriteLine("「更好」的量级还要和门槛比：本项目历史口径是 Brier 改善 ≥0.002 才算值得改动。");
        Console.WriteLine();

        // 低比分档校准
        Console.WriteLine("### 低比分档校准（预测均值 vs 实际频率，pp）");
        Console.WriteLine();
        Console.WriteLine("| ρ | 0:0 | 1:0 | 0:1 | 1:1 | 2:1 | 2:0 |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var (label, rho, a) in results)
        {
            var cells = new List<string>();
            for (var i = 0; i < LowCells.Length; i++)
            {
                var pred = a.LowPred[i] / a.N * 100;
                var hit = a.LowHit[i] / a.N * 100;
                cells.Add($"{pred:0.00} vs {hit:0.00}（{pred - hit:+0.00;-0.00}）");
            }
            Console.WriteLine($"| {label} | {string.Join(" | ", cells)} |");
        }
        Console.WriteLine();
        Console.WriteLine($"实际频率（同一批 {baseline.N} 场）：" + string.Join(" / ", LowCells.Select((c, i) =>
            $"{c.H}:{c.A} {(baseline.LowHit[i] / baseline.N * 100):0.00}%")));
        Console.WriteLine();

        Console.WriteLine("## 结论口径（代码不给结论，只给数字）");
        Console.WriteLine();
        Console.WriteLine("- 若各 ρ 的配对区间全部跨 0：这批数据**分不出**哪个 ρ 更准，−0.060 只能当「可复现的实现选择」，");
        Console.WriteLine("  不能当「更准的模型」上线；要改就等有了能分辨的样本量再说。");
        Console.WriteLine("- 若某个 ρ 的区间整体为负且 |ΔBrier| ≥ 0.002：那才是有证据的方向，仍需独立时段复核一次。");
        Console.WriteLine("- 低比分档校准看的是「0:0 被高估还是低估」，它是 ρ 最直接的作用面；");
        Console.WriteLine("  若 −0.060 把 0:0 从「低估」推到「高估」，说明幅度过大，即使总分更好也该缩小。");

        return 0;
    }

    private static ScoreSample ToSample(Row r) => new()
    {
        LeagueName = r.LeagueName,
        HomeTeam = r.HomeTeam,
        AwayTeam = r.AwayTeam,
        HomeScore = r.HomeScore,
        AwayScore = r.AwayScore,
        MatchTime = r.MatchTime
    };

    private static List<ScoreSample> Shuffle(List<ScoreSample> src, int seed)
    {
        var list = src.ToList();
        var rnd = new Random(seed);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rnd.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    private static Acc Evaluate(ScorelineModel model, List<Row> rows)
    {
        var acc = new Acc();
        foreach (var r in rows)
        {
            var dist = model.Distribution(r.LeagueName, r.HomeTeam, r.AwayTeam, null);
            if (dist.Count == 0) continue;
            var mass = dist.Sum(c => c.P);
            if (mass <= 1e-9) continue;

            var ph = Math.Min(r.HomeScore, 6);
            var pa = Math.Min(r.AwayScore, 6);
            var pActual = dist.FirstOrDefault(c => c.Home == ph && c.Away == pa).P;
            if (pActual <= 1e-12) pActual = 1e-12;

            double p3 = 0, p1 = 0, p0 = 0;
            foreach (var c in dist)
            {
                var p = c.P / mass;
                if (c.Home > c.Away) p3 += p;
                else if (c.Home == c.Away) p1 += p;
                else p0 += p;
            }

            // 总进球 0..6 + 7+
            var bands = new double[8];
            foreach (var c in dist)
            {
                var g = Math.Min(c.Home + c.Away, 7);
                bands[g] += c.P / mass;
            }

            var ll = -Math.Log(pActual);
            var b1 = Sq(p3, r.HomeScore > r.AwayScore) + Sq(p1, r.HomeScore == r.AwayScore)
                     + Sq(p0, r.HomeScore < r.AwayScore);
            var actualGoals = Math.Min(r.HomeScore + r.AwayScore, 7);
            var bjq = 0.0;
            for (var g = 0; g < 8; g++) bjq += Sq(bands[g], g == actualGoals);

            acc.LogLoss += ll;
            acc.Brier1x2 += b1;
            acc.BrierJq += bjq;
            acc.DLl.Add(ll);
            acc.DBrier.Add(b1);
            acc.N++;

            for (var i = 0; i < LowCells.Length; i++)
            {
                var (lh, la) = LowCells[i];
                var pc = dist.FirstOrDefault(c => c.Home == lh && c.Away == la).P / mass;
                acc.LowPred[i] += pc;
                acc.LowHit[i] += (r.HomeScore == lh && r.AwayScore == la) ? 1 : 0;
            }

            // 半全场：只在场次带真半场时评（HalfHomeScore 非空 = 有真半场）
            if (r.HalfHomeScore is int hh && r.HalfAwayScore is int ha)
            {
                var joint = MilkPickEngine.HalfFullJoint(model, r.LeagueName, r.HomeTeam, r.AwayTeam, null);
                if (joint is not null)
                {
                    var ht = hh > ha ? 0 : hh == ha ? 1 : 2;
                    var ft = r.HomeScore > r.AwayScore ? 0 : r.HomeScore == r.AwayScore ? 1 : 2;
                    var bb = 0.0;
                    for (var i = 0; i < 3; i++)
                    for (var j = 0; j < 3; j++)
                        bb += Sq((double)joint[i, j], i == ht && j == ft);
                    acc.BrierBq += bb;
                    acc.NBq++;
                }
            }
        }

        acc.LogLoss /= Math.Max(1, acc.N);
        acc.Brier1x2 /= Math.Max(1, acc.N);
        acc.BrierJq /= Math.Max(1, acc.N);
        acc.BrierBq /= Math.Max(1, acc.NBq);
        return acc;
    }

    private static double Sq(double p, bool hit)
    {
        var y = hit ? 1.0 : 0.0;
        return (p - y) * (p - y);
    }

    private static (double Mean, double Lo, double Hi) PairedDiff(List<double> a, List<double> b)
    {
        var n = Math.Min(a.Count, b.Count);
        if (n < 2) return (0, 0, 0);
        var mean = 0.0;
        for (var i = 0; i < n; i++) mean += a[i] - b[i];
        mean /= n;
        var var0 = 0.0;
        for (var i = 0; i < n; i++)
        {
            var d = (a[i] - b[i]) - mean;
            var0 += d * d;
        }
        var se = Math.Sqrt(var0 / (n - 1) / n);
        return (mean, mean - 1.96 * se, mean + 1.96 * se);
    }
}
