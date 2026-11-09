using AiFootBall.Core.Models;
using AiFootBall.Data;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 半全场「下半场 λ 按排名条件化」的 walk-forward Brier 验证（2026-11-08）。
///
/// 为什么要单独做这一台：11-07 三场大奶（007/008 胜负、027 负平）都是「排名靠后的一方先进球」，
/// 事后能复盘出「领先方强弱会影响下半场走势」，但这条规律要进引擎必须先证明它**在样本外**能降低误差，
/// 否则就是在做「把已发生的比赛解释一遍」的自欺。核心风险是 **λ 里已经含了强弱**（Dixon-Coles 的攻防强度），
/// 再按排名乘一次就是双重计算——所以宁可只测不上。
///
/// 口径：
///   · 训练集 = FbMatch 里 MatchTime &lt; 2025-01-01 的全部完赛场次（严格前向，测试集不泄漏）
///   · 测试集 = 2025-01-01 之后、有 FbMatchRank 赛前排名、且**半场数据可信**的联赛
///     （半场=全场占比落在 10%~45% 且样本 ≥300；球会友谊 97.8%、西协丙 99.98% 这类整类弃用）
///   · 主口径再要求两队本联赛样本 ≥5（与线上出注闸一致），否则模型退先验，比的不是模型
///
/// 用法：dotnet run --project src/AiFootBall.Backtest -c Release -- hf-lambda
/// </summary>
public static class HalfFullLambdaStudy
{
    /// <summary>测试集起点。训练集严格早于它。</summary>
    private static readonly DateTime Cutoff = new(2025, 1, 1);

    /// <summary>上下半场进球份额（线上 HalfGoalShare = 0.45）。本次不动它，只动下半场强弱乘子。</summary>
    private const double Share = 0.45;

    /// <summary>判定「领先方明显更强/更弱」的分位差阈值，避免势均力敌的场次被误乘。</summary>
    private const double Eps = 0.02;

    private const int MaxGoals = 6;

    private sealed class Row
    {
        public long MatchId { get; set; }
        public string? LeagueName { get; set; }
        public string HomeTeam { get; set; } = "";
        public string AwayTeam { get; set; } = "";
        public DateTime MatchTime { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
        public int HalfHomeScore { get; set; }
        public int HalfAwayScore { get; set; }
        public int HomeRankPre { get; set; }
        public int AwayRankPre { get; set; }
        public int TeamCount { get; set; }
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

    /// <summary>一个待评估变体：领先方为强队时攻×StrongAtk，为弱队时攻×WeakAtk；Scaled=按分位差线性缩放。</summary>
    private sealed record Variant(string Name, double StrongAtk, double WeakAtk, bool Scaled);

    public static async Task<int> RunAsync(string connStr)
    {
        var factory = new SqlConnectionFactory(connStr);
        await using var conn = await factory.OpenAsync();

        Console.WriteLine($"# 半全场下半场 λ 条件化：walk-forward Brier 验证  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
        Console.WriteLine("> 只读库，不改线上引擎。目的：在样本外判断「按排名调下半场 λ」是真改进还是双重计算。");
        Console.WriteLine();

        var train = (await conn.QueryAsync<Sample>(
            @"SELECT LeagueName, HomeTeamName AS HomeTeam, AwayTeamName AS AwayTeam,
                     MatchTime, CAST(HomeScore AS int) AS HomeScore, CAST(AwayScore AS int) AS AwayScore
              FROM dbo.FbMatch
              WHERE HomeScore IS NOT NULL AND AwayScore IS NOT NULL AND MatchTime < @cutoff",
            new { cutoff = Cutoff })).ToList();

        var test = (await conn.QueryAsync<Row>(
            @"WITH lg AS (
                  SELECT LeagueName, COUNT(*) AS N,
                         1.0 * SUM(CASE WHEN HalfHomeScore = HomeScore AND HalfAwayScore = AwayScore THEN 1 ELSE 0 END)
                             / COUNT(*) AS PctSame
                  FROM dbo.FbMatch
                  WHERE HomeScore IS NOT NULL AND AwayScore IS NOT NULL
                    AND HalfHomeScore IS NOT NULL AND HalfAwayScore IS NOT NULL
                  GROUP BY LeagueName
              )
              SELECT m.MatchId, m.LeagueName, m.HomeTeamName AS HomeTeam, m.AwayTeamName AS AwayTeam, m.MatchTime,
                     CAST(m.HomeScore AS int) AS HomeScore, CAST(m.AwayScore AS int) AS AwayScore,
                     CAST(m.HalfHomeScore AS int) AS HalfHomeScore, CAST(m.HalfAwayScore AS int) AS HalfAwayScore,
                     r.HomeRankPre, r.AwayRankPre, r.TeamCount
              FROM dbo.FbMatch m
              JOIN lg ON lg.LeagueName = m.LeagueName AND lg.N >= 300 AND lg.PctSame BETWEEN 0.10 AND 0.45
              JOIN dbo.FbMatchRank r ON r.MatchId = m.MatchId
              WHERE m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                AND m.HalfHomeScore IS NOT NULL AND m.HalfAwayScore IS NOT NULL
                AND m.MatchTime >= @cutoff",
            new { cutoff = Cutoff })).ToList();

        if (train.Count < 1000 || test.Count < 500)
        {
            Console.WriteLine($"样本不足：训练 {train.Count} 场 / 测试 {test.Count} 场。");
            return 1;
        }

        var model = ScorelineModel.Fit(train.Select(s => new ScoreSample
        {
            LeagueName = s.LeagueName, HomeTeam = s.HomeTeam, AwayTeam = s.AwayTeam,
            HomeScore = s.HomeScore, AwayScore = s.AwayScore
        }).ToList());

        if (!model.Ready)
        {
            Console.WriteLine("模型未就绪。");
            return 1;
        }

        Console.WriteLine("| 项目 | 值 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| 训练集（< {Cutoff:yyyy-MM-dd}） | {train.Count} 场 |");
        Console.WriteLine($"| 测试集（≥ {Cutoff:yyyy-MM-dd}，可信半场联赛 + 有赛前排名） | {test.Count} 场 |");
        Console.WriteLine($"| 上半场进球份额 | {Share:F2}（线上值，本次不动） |");
        Console.WriteLine();

        var variants = new List<Variant>
        {
            new("基线（现线上口径，不条件化）", 1.00, 1.00, false),
            new("保守 ×1.05 / ×0.95", 1.05, 0.95, false),
            new("中等 ×1.10 / ×0.90", 1.10, 0.90, false),
            new("记忆值 ×1.17 / ×0.83", 1.17, 0.83, false),
            new("激进 ×1.25 / ×0.75", 1.25, 0.75, false),
            new("记忆值×分位差缩放", 1.17, 0.83, true)
        };

        // 逐场先落一条「与变体无关」的记录，避免每个变体重复查模型
        var cases = new List<Case>();
        foreach (var r in test)
        {
            var eg = model.ExpectedGoals(r.LeagueName, r.HomeTeam, r.AwayTeam, null);
            if (eg is null) continue;

            var games = Math.Min(model.TeamGames(r.LeagueName, r.HomeTeam), model.TeamGames(r.LeagueName, r.AwayTeam));
            var denom = Math.Max(1, r.TeamCount - 1);
            var homePct = (r.HomeRankPre - 1.0) / denom;
            var awayPct = (r.AwayRankPre - 1.0) / denom;
            var diff = awayPct - homePct;   // >0 = 主队排名更靠前（更强）

            var halfRes = Res(r.HalfHomeScore, r.HalfAwayScore);
            var actual = Index(halfRes, Res(r.HomeScore, r.AwayScore));

            cases.Add(new Case
            {
                MatchId = r.MatchId,
                League = r.LeagueName ?? "",
                Lh = eg.Value.Home,
                La = eg.Value.Away,
                Diff = diff,
                MinGames = games,
                Known = model.KnowsTeam(r.LeagueName, r.HomeTeam) && model.KnowsTeam(r.LeagueName, r.AwayTeam),
                ActualIndex = actual,
                HalfLeader = r.HalfHomeScore > r.HalfAwayScore ? 1 : r.HalfHomeScore < r.HalfAwayScore ? -1 : 0,
                ActualMargin = r.HomeScore - r.AwayScore,
                HalfMargin = r.HalfHomeScore - r.HalfAwayScore,
                MatchTime = r.MatchTime
            });
        }

        Console.WriteLine($"模型能给出进球期望的场次 {cases.Count} 场；其中两队都认识 {cases.Count(c => c.Known)} 场、" +
                          $"两队本联赛样本都 ≥5 场 {cases.Count(c => c.MinGames >= 5)} 场。");
        Console.WriteLine();

        // ---------- 一、先看事实：领先方强弱与守住率 ----------
        Console.WriteLine("## 一、先确认现象本身（不谈模型，只看实际）");
        Console.WriteLine();
        Console.WriteLine("「强/弱」= 半场领先方的排名分位相对对手，差值 ≥ 0.02 才算明显。");
        Console.WriteLine();
        Console.WriteLine("| 半场领先方 | 场次 | 终场仍赢 | 被追平 | 被逆转 |");
        Console.WriteLine("|---|---|---|---|---|");
        foreach (var (label, sel) in new (string, Func<Case, bool>)[]
                 {
                     ("强队领先", c => c.HalfLeader != 0 && c.LeaderIsStronger),
                     ("弱队领先", c => c.HalfLeader != 0 && !c.LeaderIsStronger),
                     ("半场平局", c => c.HalfLeader == 0)
                 })
        {
            var sub = cases.Where(sel).ToList();
            if (sub.Count == 0) continue;
            var win = sub.Count(c => Math.Sign(c.ActualMargin) == Math.Sign(c.HalfMargin));
            var drew = sub.Count(c => c.ActualMargin == 0 && c.HalfMargin != 0);
            var lost = sub.Count - win - drew;
            Console.WriteLine($"| {label} | {sub.Count} | {100.0 * win / sub.Count:F1}% | {100.0 * drew / sub.Count:F1}% | {100.0 * lost / sub.Count:F1}% |");
        }
        Console.WriteLine();
        Console.WriteLine("注：「终场仍赢」按领先方向计（客队领先则终场客胜也算赢），被逆转=终场落后方反超。");
        Console.WriteLine();

        // ---------- 二、Brier 主表 ----------
        var subsets = new (string Label, Func<Case, bool> Select)[]
        {
            ("全部", _ => true),
            ("主队排名更靠前", c => c.Diff > Eps),
            ("主队排名更靠后", c => c.Diff < -Eps),
            ("实力差大（|Δ|≥0.33）", c => Math.Abs(c.Diff) >= 0.33),
            ("半场有领先方", c => c.HalfLeader != 0)
        };

        Console.WriteLine("## 二、9 档半全场 Brier（越低越好），主口径 = 两队本联赛样本都 ≥5");
        Console.WriteLine();
        var main = cases.Where(c => c.Known && c.MinGames >= 5).ToList();
        Console.WriteLine($"主口径场次 {main.Count} 场。");
        Console.WriteLine();
        PrintBrierTable(main, variants, subsets, reverseOnly: false);

        Console.WriteLine();
        Console.WriteLine("## 三、只看反转 6 档（胜负/负胜/平胜/平负/胜平/负平，重新归一后算 Brier）");
        Console.WriteLine();
        Console.WriteLine("> 反转档才是我们真正下注的地方；9 档 Brier 会被胜胜/平平/负负三个高概率档稀释。");
        Console.WriteLine();
        PrintBrierTable(main, variants, subsets, reverseOnly: true);

        // ---------- 四、配对显著性 ----------
        Console.WriteLine();
        Console.WriteLine("## 四、配对检验：变体 − 基线（负 = 变体更好）");
        Console.WriteLine();
        Console.WriteLine("主口径 + 半场有领先方的场次（这是唯一受影响的子集），逐场 Brier 差求均值与标准误。");
        Console.WriteLine();
        Console.WriteLine("| 变体 | 场次 | 平均 Brier 差 | 标准误 | t | 判定 |");
        Console.WriteLine("|---|---|---|---|---|---|");
        var leadCases = main.Where(c => c.HalfLeader != 0).ToList();
        foreach (var v in variants.Skip(1))
        {
            var deltas = new List<double>(leadCases.Count);
            foreach (var c in leadCases)
            {
                var b = Brier(Joint(c, variants[0]), c.ActualIndex);
                var x = Brier(Joint(c, v), c.ActualIndex);
                deltas.Add(x - b);
            }
            var mean = deltas.Average();
            var se = StdErr(deltas);
            var t = se <= 1e-12 ? 0 : mean / se;
            var verdict = Math.Abs(t) < 1.96 ? "不显著" : (t < 0 ? "显著更好" : "显著更差");
            Console.WriteLine($"| {v.Name} | {deltas.Count} | {mean:+0.00000;-0.00000} | {se:F5} | {t:+0.00;-0.00} | {verdict} |");
        }
        Console.WriteLine();

        // ---------- 五、判定 ----------
        Console.WriteLine("## 五、判定（上线门槛：总分低 ≥0.002 且三个子集方向一致）");
        Console.WriteLine();
        Console.WriteLine("| 变体 | 全部 ΔBrier | 主强 Δ | 主弱 Δ | 三子集同向 | 幅度达标 | 结论 |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var v in variants.Skip(1))
        {
            var dAll = Delta(main, v, variants[0], _ => true);
            var dHome = Delta(main, v, variants[0], c => c.Diff > Eps);
            var dAway = Delta(main, v, variants[0], c => c.Diff < -Eps);
            var sameDir = (dAll < 0 && dHome < 0 && dAway < 0) || (dAll > 0 && dHome > 0 && dAway > 0);
            var bigEnough = dAll <= -0.002;
            var verdict = !sameDir ? "方向不一致，弃"
                : !bigEnough ? "幅度不足，弃"
                : "**可考虑上线**";
            Console.WriteLine($"| {v.Name} | {dAll:+0.00000;-0.00000} | {dHome:+0.00000;-0.00000} | {dAway:+0.00000;-0.00000} | {(sameDir ? "是" : "否")} | {(bigEnough ? "是" : "否")} | {verdict} |");
        }
        Console.WriteLine();

        // ---------- 六、诊断：改善到底来自哪几档，方向有没有搞反 ----------
        // 主表只看总分会被稀释：9 档里胜胜/平平/负负占大头，条件化只要把这三档调准一点，
        // 总分就会「变好」，而我们真正下注的反转 6 档可能同时在变坏。这一节就把它拆开。
        // 同时挂一个**反向乘子**（强队领先反而收着踢）作对照：如果反向明显更差，才说明方向没搞反。
        Console.WriteLine("## 六、诊断：改善来自哪几档，方向有没有搞反");
        Console.WriteLine();
        var diag = new List<Variant>
        {
            variants[0],
            variants[3],
            variants[4],
            new("反向对照 ×0.83 / ×1.17", 0.83, 1.17, false),
            new("更反向对照 ×0.75 / ×1.25", 0.75, 1.25, false)
        };

        Console.WriteLine("| 变体 | 顺风 3 档 Brier | 反转 6 档 Brier | 半场主队领先 (n) | 半场客队领先 (n) |");
        Console.WriteLine("|---|---|---|---|---|");
        var homeLead = main.Where(c => c.HalfLeader > 0).ToList();
        var awayLead = main.Where(c => c.HalfLeader < 0).ToList();
        foreach (var v in diag)
        {
            var straight = main.Average(c => Brier(Joint(c, v), c.ActualIndex, mode: BrierMode.Straight));
            var reverse = main.Average(c => Brier(Joint(c, v), c.ActualIndex, mode: BrierMode.Reverse));
            var hl = homeLead.Count == 0 ? double.NaN : homeLead.Average(c => Brier(Joint(c, v), c.ActualIndex));
            var al = awayLead.Count == 0 ? double.NaN : awayLead.Average(c => Brier(Joint(c, v), c.ActualIndex));
            Console.WriteLine($"| {v.Name} | {straight:F5} | {reverse:F5} | {hl:F5} ({homeLead.Count}) | {al:F5} ({awayLead.Count}) |");
        }
        Console.WriteLine();

        Console.WriteLine("逐档 Brier 贡献（每场该档 (p−y)² 的均值，越小越好）：");
        Console.WriteLine();
        Console.WriteLine("| 档位 | 基线 | 记忆值 ×1.17/×0.83 | 差 |");
        Console.WriteLine("|---|---|---|---|");
        string[] names = { "胜胜", "胜平", "胜负", "平胜", "平平", "平负", "负胜", "负平", "负负" };
        for (var i = 0; i < 9; i++)
        {
            var b0 = main.Average(c => CellBrier(Joint(c, variants[0]), c.ActualIndex, i));
            var b1 = main.Average(c => CellBrier(Joint(c, variants[3]), c.ActualIndex, i));
            Console.WriteLine($"| {names[i]} | {b0:F5} | {b1:F5} | {b1 - b0:+0.00000;-0.00000} |");
        }
        Console.WriteLine();
        Console.WriteLine("读法：负=该档变准，正=该档变差。反转 6 档若普遍为正，说明这条调整在**我们下注的地方**是有害的。");
        Console.WriteLine();

        return 0;
    }

    private sealed class Case
    {
        public long MatchId { get; set; }
        public string League { get; set; } = "";
        public double Lh { get; set; }
        public double La { get; set; }
        public double Diff { get; set; }
        public int MinGames { get; set; }
        public bool Known { get; set; }
        public int ActualIndex { get; set; }
        public int HalfLeader { get; set; }        // 1=主队领先 / 0=平 / -1=客队领先
        public int HalfMargin { get; set; }
        public int ActualMargin { get; set; }
        public DateTime MatchTime { get; set; }

        /// <summary>半场领先方是不是排名更靠前的一方。</summary>
        public bool LeaderIsStronger =>
            HalfLeader > 0 ? Diff > Eps : HalfLeader < 0 && Diff < -Eps;
    }

    private static void PrintBrierTable(List<Case> cases, List<Variant> variants,
        (string Label, Func<Case, bool> Select)[] subsets, bool reverseOnly)
    {
        Console.WriteLine("| 变体 | " + string.Join(" | ", subsets.Select(s => $"{s.Label} ({cases.Count(s.Select)})")) + " |");
        Console.WriteLine("|---" + string.Concat(subsets.Select(_ => "|---")) + "|");
        foreach (var v in variants)
        {
            var cells = new List<string>();
            foreach (var s in subsets)
            {
                var sub = cases.Where(s.Select).ToList();
                if (sub.Count == 0) { cells.Add("—"); continue; }
                var brier = sub.Average(c => Brier(Joint(c, v), c.ActualIndex, reverseOnly ? BrierMode.Reverse : BrierMode.All));
                cells.Add(brier.ToString("F5"));
            }
            Console.WriteLine($"| {v.Name} | " + string.Join(" | ", cells) + " |");
        }
        Console.WriteLine();
    }

    private static double Delta(List<Case> cases, Variant v, Variant baseline,
        Func<Case, bool> select)
    {
        var sub = cases.Where(select).ToList();
        if (sub.Count == 0) return 0;
        return sub.Average(c => Brier(Joint(c, v), c.ActualIndex))
             - sub.Average(c => Brier(Joint(c, baseline), c.ActualIndex));
    }

    private static double StdErr(List<double> xs)
    {
        if (xs.Count < 2) return 0;
        var mean = xs.Average();
        var varSum = xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1);
        return Math.Sqrt(varSum / xs.Count);
    }

    /// <summary>9 档联合分布。下半场按「半场领先方是强队/弱队」分别换进攻乘子。</summary>
    private static double[] Joint(Case c, Variant v)
    {
        var first = Grid(c.Lh * Share, c.La * Share);
        var draw = Grid(c.Lh * (1 - Share), c.La * (1 - Share));

        // 主队领先时，领先方(主队)的强度优势 = Diff；客队领先时，领先方(客队)的优势 = -Diff。
        // 乘子只落在领先方自己的进攻 λ 上。
        var mHomeLead = LeaderMult(v, c.Diff);
        var mAwayLead = LeaderMult(v, -c.Diff);
        var leadHome = Grid(c.Lh * (1 - Share) * mHomeLead, c.La * (1 - Share));
        var leadAway = Grid(c.Lh * (1 - Share), c.La * (1 - Share) * mAwayLead);

        var acc = new double[9];
        foreach (var f in first)
        {
            var ht = f.H > f.A ? 0 : f.H == f.A ? 1 : 2;
            var sg = ht == 0 ? leadHome : ht == 1 ? draw : leadAway;
            foreach (var s in sg)
            {
                var fh = f.H + s.H;
                var fa = f.A + s.A;
                var ft = fh > fa ? 0 : fh == fa ? 1 : 2;
                acc[ht * 3 + ft] += f.P * s.P;
            }
        }

        var mass = acc.Sum();
        if (mass > 0) for (var i = 0; i < 9; i++) acc[i] /= mass;
        return acc;
    }

    /// <summary>
    /// 领先方的下半场进攻乘子。<paramref name="leaderAdvantage"/> 是「领先方相对对手的排名分位优势」：
    /// &gt;0 = 领先的是一支排名更靠前的球队（该压上还是该收着，市场给的答案不同）。
    /// 持平时（|值| ≤ <see cref="Eps"/>）一律返回 1，不做任何调整。
    /// Scaled 模式下幅度按分位差线性缩放，避免「2 名打 20 名」和「12 名打 13 名」被同样乘一次。
    /// </summary>
    private static double LeaderMult(Variant v, double leaderAdvantage)
    {
        if (Math.Abs(leaderAdvantage) <= Eps) return 1;
        var k = leaderAdvantage > 0 ? v.StrongAtk : v.WeakAtk;
        var strength = v.Scaled ? Math.Min(1.0, Math.Abs(leaderAdvantage)) : 1.0;
        return 1 + (k - 1) * strength;
    }

    private static List<(int H, int A, double P)> Grid(double lh, double la)
    {
        var cells = new List<(int H, int A, double P)>((MaxGoals + 1) * (MaxGoals + 1));
        for (var h = 0; h <= MaxGoals; h++)
        for (var a = 0; a <= MaxGoals; a++)
            cells.Add((h, a, Poisson(h, lh) * Poisson(a, la)));
        return cells;
    }

    private enum BrierMode { All, Reverse, Straight }

    private static double Brier(double[] p, int actual, BrierMode mode = BrierMode.All)
    {
        int[]? idx = mode switch
        {
            // 反转 6 档：胜负(2) 负胜(6) 平胜(3) 平负(5) 胜平(1) 负平(7)
            BrierMode.Reverse => new[] { 2, 6, 3, 5, 1, 7 },
            // 顺风 3 档：胜胜(0) 平平(4) 负负(8)
            BrierMode.Straight => new[] { 0, 4, 8 },
            _ => null
        };

        if (idx is null)
        {
            var s = 0.0;
            for (var i = 0; i < 9; i++)
            {
                var y = i == actual ? 1.0 : 0.0;
                s += (p[i] - y) * (p[i] - y);
            }
            return s;
        }

        // 子集内重新归一后再算，否则「只算 6 档」的分母不对
        var mass = idx.Sum(i => p[i]);
        if (mass <= 1e-12) return 1.0;
        var sum = 0.0;
        foreach (var i in idx)
        {
            var q = p[i] / mass;
            var y = i == actual ? 1.0 : 0.0;
            sum += (q - y) * (q - y);
        }
        return sum;
    }

    /// <summary>单档 Brier 贡献：(p_i − 1[i=实际])²，用来定位改善/恶化落在哪一档。</summary>
    private static double CellBrier(double[] p, int actual, int index)
    {
        var y = index == actual ? 1.0 : 0.0;
        return (p[index] - y) * (p[index] - y);
    }

    /// <summary>半全场 9 档索引：行=半场(3/1/0)，列=全场(3/1/0)。</summary>
    private static int Index(char half, char full)
    {
        int r = half == '3' ? 0 : half == '1' ? 1 : 2;
        int c = full == '3' ? 0 : full == '1' ? 1 : 2;
        return r * 3 + c;
    }

    private static char Res(int h, int a) => h > a ? '3' : h == a ? '1' : '0';

    private static double Poisson(int k, double lambda)
    {
        if (k < 0) return 0;
        if (lambda < 1e-6) return k == 0 ? 1 : 0;
        var p = Math.Exp(-lambda);
        for (var i = 1; i <= k; i++) p *= lambda / i;
        return p;
    }
}
