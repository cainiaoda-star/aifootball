using AiFootBall.Core.Models;
using AiFootBall.Data;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 「时间衰减」与「模型/市场混合权重」的联合 A/B（2026-11-11 加，用户批准做第 1 件）。
///
/// 要回答两个问题，且**必须一起回答**，因为它们的效应会互相掩盖：
///
/// 1. 18 个月等权到底该不该改成按时间衰减？现在 <c>GetScoreSamplesAsync</c> 取近 18 个月、
///    每场权重相同，两年前的比赛和上个月的比赛对 λ 的贡献一样大。换教练、保级队变强的变化
///    模型看不见。这是用户方案里唯一一条本项目确实缺失的东西。
///
/// 2. 市场权重方向对不对？（2026-11-11 已判定「样本不足」，此工具转入停用状态）
///    当时的写法是按各玩法统一 0.3（即 0.7 模型 + 0.3 市场）。实测 Brier 模型 0.5995 vs
///    市场 0.5812，市场更准，所以权重方向该往市场挪。
///    ★ 但 Brier 定不了下注用的 w：`weight-replay` 验证段只有 216 注，
///    标准误 ±0.16 而组间差只有 0.03~0.09，分辨不了；两段排序还相反。
///    所以 2026-11-11 起 w 按先验冻结（见 MilkPickEngine.MarketWeightSpf 等常量），
///    **未从 ROI 选出**，这个工具只留着记录当时怎么算的，不要再拿它调参。
///
/// 关键设计取舍（不写清楚就会被误读）：
/// · 衰减原点 = **切分时刻**，不是 DateTime.Now。用 now 会把整段训练历史一起压扁，
///   衰减有没有用就看不出来了。代价是测试集里越靠后的场次、训练数据的「相对年龄」越大，
///   这个近似会**低估**衰减的收益，不会高估。
/// · 评估只算「两队训练样本都 ≥ 5 场」的场次。衰减不改变这个集合（Games 仍是真实场次），
///   所以六个模型比的是同一批比赛；不这么筛，衰减会让更多队退回联赛均值，
///   于是「模型变准」和「覆盖率变低」混在一起，分不清是谁在起作用。
/// · 市场基准给两套：初盘与收盘。只用初盘会低估市场（于是高估模型），
///   只用收盘又和历史报告（0.5995/0.5812 是初盘口径）对不上。两套并列，谁也别蒙谁。
///
/// 用法：dotnet run --project src/AiFootBall.Backtest -c Release -- weight-probe [训练占比]
/// 只读库，不改任何业务表。
/// </summary>
public static class ModelWeightStudy
{
    sealed class WRow
    {
        public long MatchId { get; set; }
        public string? LeagueName { get; set; }
        public string HomeTeam { get; set; } = "";
        public string AwayTeam { get; set; } = "";
        public DateTime MatchTime { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
        public decimal OpenH { get; set; }
        public decimal OpenD { get; set; }
        public decimal OpenA { get; set; }
        public decimal? CloseH { get; set; }
        public decimal? CloseD { get; set; }
        public decimal? CloseA { get; set; }
    }

    sealed class SRow
    {
        public string? LeagueName { get; set; }
        public string HomeTeam { get; set; } = "";
        public string AwayTeam { get; set; } = "";
        public DateTime MatchTime { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
    }

    /// <summary>半衰期候选（天）。null = 等权，即线上现状。</summary>
    static readonly (double? HalfLife, string Label)[] HalfLives =
    {
        (null, "等权（现状）"),
        (540, "540 天（=18 个月窗口，几乎不衰减）"),
        (365, "365 天"),
        (240, "240 天"),
        (180, "180 天"),
        (120, "120 天"),
        (90,  "90 天"),
        (60,  "60 天")
    };

    static readonly decimal[] MarketWeights = { 0m, 0.3m, 0.5m, 0.7m, 1.0m };

    /// <summary>改善门槛：与项目其他判断同源（calibration 报告也是 0.002 才算优于市场）。</summary>
    const double MeaningfulGain = 0.002;

    public static async Task<int> RunAsync(string connStr, double trainRatio)
    {
        var factory = new SqlConnectionFactory(connStr);
        await using var conn = await factory.OpenAsync();

        var samples = (await conn.QueryAsync<SRow>(
            @"SELECT LeagueName, HomeTeamName AS HomeTeam, AwayTeamName AS AwayTeam, MatchTime,
                     HomeScore, AwayScore
              FROM dbo.FbMatch
              WHERE Status = 3 AND HomeScore IS NOT NULL AND AwayScore IS NOT NULL
                AND MatchTime >= DATEADD(month, -18, GETDATE())")).ToList();

        var rows = (await conn.QueryAsync<WRow>(
            @"SELECT m.MatchId, m.LeagueName,
                     m.HomeTeamName AS HomeTeam, m.AwayTeamName AS AwayTeam, m.MatchTime,
                     m.HomeScore, m.AwayScore,
                     o.HomeWin AS OpenH, o.Draw AS OpenD, o.AwayWin AS OpenA,
                     c.HomeWin AS CloseH, c.Draw AS CloseD, c.AwayWin AS CloseA
              FROM dbo.FbMatch m
              OUTER APPLY (SELECT TOP 1 HomeWin, Draw, AwayWin FROM dbo.FbOdds
                           WHERE MatchId = m.MatchId AND OddsType = 'EU' AND CompanyId = 0
                             AND IsOpening = 1 AND HomeWin > 1 AND Draw > 1 AND AwayWin > 1
                           ORDER BY OddsId DESC) o
              OUTER APPLY (SELECT TOP 1 HomeWin, Draw, AwayWin FROM dbo.FbOdds
                           WHERE MatchId = m.MatchId AND OddsType = 'EU' AND CompanyId = 0
                             AND IsOpening = 0 AND HomeWin > 1 AND Draw > 1 AND AwayWin > 1
                             AND CapturedAt <= m.MatchTime
                           ORDER BY CapturedAt DESC) c
              WHERE m.Status = 3 AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                AND o.HomeWin IS NOT NULL
              ORDER BY m.MatchTime")).ToList();

        if (samples.Count < 500 || rows.Count < 200)
        {
            Console.WriteLine($"样本不足（完赛 {samples.Count} / 有初盘 {rows.Count}），跑不了。");
            return 1;
        }

        var times = rows.Select(r => r.MatchTime).OrderBy(t => t).ToList();
        var cutIndex = Math.Max(1, Math.Min(times.Count - 1, (int)(times.Count * trainRatio)));
        var cutoff = times[cutIndex];

        var trainSamples = samples.Where(s => s.MatchTime < cutoff)
            .Select(s => new ScoreSample
            {
                LeagueName = s.LeagueName,
                HomeTeam = s.HomeTeam,
                AwayTeam = s.AwayTeam,
                HomeScore = s.HomeScore,
                AwayScore = s.AwayScore,
                MatchTime = s.MatchTime
            }).ToList();
        var testAll = rows.Where(r => r.MatchTime >= cutoff).ToList();

        // 只保留「两队训练样本都 ≥ 5 场」的场次——六个模型共用同一批。
        var gamesInTrain = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in trainSamples)
        {
            var h = TeamAliases.Canonical(s.HomeTeam);
            var a = TeamAliases.Canonical(s.AwayTeam);
            gamesInTrain[h] = gamesInTrain.GetValueOrDefault(h) + 1;
            gamesInTrain[a] = gamesInTrain.GetValueOrDefault(a) + 1;
        }
        int Known(string n) => gamesInTrain.GetValueOrDefault(TeamAliases.Canonical(n));
        var test = testAll.Where(r => Math.Min(Known(r.HomeTeam), Known(r.AwayTeam)) >= 5).ToList();

        Console.WriteLine($"# 时间衰减 × 混合权重 A/B  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
        Console.WriteLine("| 项目 | 值 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| 全库完赛场次（18 个月） | {samples.Count} |");
        Console.WriteLine($"| 有初盘欧赔场次 | {rows.Count} |");
        Console.WriteLine($"| 切分时刻 | {cutoff:yyyy-MM-dd HH:mm} |");
        Console.WriteLine($"| 训练样本 | {trainSamples.Count} 场（截至切分时刻） |");
        Console.WriteLine($"| 测试场次 | {test.Count} 场（两队训练样本都 ≥ 5） |");
        Console.WriteLine();
        Console.WriteLine("衰减原点取**切分时刻**：训练只用截止日前数据，等价于「每天重算、只看过去」。");
        Console.WriteLine("测试集越靠后的场次相对年龄越大，因此这里测出的衰减收益是**下限**。");
        Console.WriteLine();

        // ---- 市场基准（与模型无关，只算一次）----
        var mk = Stats.Empty();
        var mkClose = Stats.Empty();
        foreach (var r in test)
        {
            var (mh, md, ma) = Fair(r.OpenH, r.OpenD, r.OpenA);
            mk.Add(mh, md, ma, r);
            if (r.CloseH is decimal ch && r.CloseD is decimal cd && r.CloseA is decimal ca)
            {
                var (h2, d2, a2) = Fair(ch, cd, ca);
                mkClose.Add(h2, d2, a2, r);
            }
        }

        Console.WriteLine("## 零、市场基准（先摆在这里，模型有没有资格谈权重看它）");
        Console.WriteLine();
        Console.WriteLine("| 市场口径 | 场次 | Brier | LogLoss |");
        Console.WriteLine("|---|---:|---:|---:|");
        Console.WriteLine($"| 初盘欧赔去水 | {mk.N} | {mk.Brier:F4} | {mk.Log:F4} |");
        Console.WriteLine($"| 收盘欧赔去水 | {mkClose.N} | {mkClose.Brier:F4} | {mkClose.Log:F4} |");
        Console.WriteLine();

        // ---- 主表 ----
        var results = new List<(string Label, double? HalfLife, decimal W, double Brier, double Log, int N, double Std)>();

        Console.WriteLine("## 一、逐组结果");
        Console.WriteLine();
        Console.WriteLine("| 半衰期 | 有效样本 | 权重 0.0 | 0.3（现状） | 0.5 | 0.7 | 1.0（纯市场） |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|");

        foreach (var (halfLife, label) in HalfLives)
        {
            var model = ScorelineModel.Fit(trainSamples, halfLifeDays: halfLife, asOf: cutoff);
            if (!model.Ready)
            {
                Console.WriteLine($"| {label} | — | 模型未就绪 | | | | |");
                continue;
            }

            // 有效样本 = Σ 0.5^(年龄/半衰期)，用来直观看出「这等于只用了多少场」。
            var effN = 0.0;
            foreach (var s in trainSamples)
            {
                if (halfLife is double hl && hl > 0 && s.MatchTime is DateTime t)
                    effN += Math.Pow(0.5, Math.Max(0, (cutoff - t).TotalDays) / hl);
                else
                    effN += 1;
            }

            var cells = new List<(double Mh, double Md, double Ma, WRow R)>(test.Count);
            var pHs = new List<double>(test.Count);
            foreach (var r in test)
            {
                var dist = model.Distribution(r.LeagueName, r.HomeTeam, r.AwayTeam, null);
                if (dist.Count == 0) continue;
                double ph = 0, pd = 0, pa = 0;
                foreach (var (h, a, p) in dist)
                {
                    if (h > a) ph += p;
                    else if (h == a) pd += p;
                    else pa += p;
                }
                var sum = ph + pd + pa;
                if (sum <= 0) continue;
                ph /= sum; pd /= sum; pa /= sum;
                cells.Add((ph, pd, pa, r));
                pHs.Add(ph);
            }
            if (cells.Count == 0) { Console.WriteLine($"| {label} | {effN:F0} | 无有效场次 | | | | |"); continue; }

            var std = StdDev(pHs);
            var row = new List<string> { label, effN.ToString("F0") };
            foreach (var w in MarketWeights)
            {
                var st = Stats.Empty();
                foreach (var (ph, pd, pa, r) in cells)
                {
                    var (mh, md, ma) = Fair(r.OpenH, r.OpenD, r.OpenA);
                    var bw = (double)w;
                    st.Add(ph * (1 - bw) + mh * bw,
                           pd * (1 - bw) + md * bw,
                           pa * (1 - bw) + ma * bw, r);
                }
                results.Add((label, halfLife, w, st.Brier, st.Log, st.N, std));
                row.Add($"{st.Brier:F4}");
            }
            Console.WriteLine("| " + string.Join(" | ", row) + " |");
        }
        Console.WriteLine();
        Console.WriteLine($"Brier 越小越好；括号外的改善门槛是 **{MeaningfulGain:F3}**（与校准报告的判据同源）。");
        Console.WriteLine();

        // ---- 二、相对现状的差值 ----
        var baseline = results.FirstOrDefault(x => x.HalfLife is null && x.W == 0.3m);
        Console.WriteLine("## 二、相对「等权 + 权重 0.3」的差值（负数=更好）");
        Console.WriteLine();
        Console.WriteLine("| 半衰期 | 权重 0.0 | 0.3（现状） | 0.5 | 0.7 | 1.0 |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|");
        foreach (var (halfLife, label) in HalfLives)
        {
            var group = results.Where(x => x.HalfLife == halfLife).ToList();
            if (group.Count == 0) continue;
            var cells = new List<string> { label };
            foreach (var w in MarketWeights)
            {
                var hit = group.FirstOrDefault(x => x.W == w);
                if (hit.Label is null) { cells.Add("—"); continue; }
                var d = hit.Brier - baseline.Brier;
                cells.Add($"{d:+0.0000;-0.0000;0.0000}");
            }
            Console.WriteLine("| " + string.Join(" | ", cells) + " |");
        }
        Console.WriteLine();

        // ---- 三、结论 ----
        Console.WriteLine("## 三、结论");
        Console.WriteLine();
        var best = results.OrderBy(x => x.Brier).First();
        var gain = baseline.Brier - best.Brier;
        Console.WriteLine($"- 现状（等权 + 0.3）：Brier **{baseline.Brier:F4}** / LogLoss {baseline.Log:F4}。");
        Console.WriteLine($"- 最优组合：**{best.Label} + 权重 {best.W:0.0}**，Brier **{best.Brier:F4}** / LogLoss {best.Log:F4}，改善 **{gain:+0.0000;-0.0000}**。");
        Console.WriteLine($"- 判定：{(gain >= MeaningfulGain ? $"**过门槛**（≥ {MeaningfulGain:F3}），值得改。" : $"**没过门槛**（< {MeaningfulGain:F3}），不改。")}");
        Console.WriteLine();

        // 权重方向单独判一次：在同一个半衰期下，权重 0.7 / 1.0 是不是比 0.3 好。
        var sameDecay = results.Where(x => x.HalfLife == baseline.HalfLife).ToList();
        var w03 = sameDecay.First(x => x.W == 0.3m);
        var w07 = sameDecay.First(x => x.W == 0.7m);
        var w10 = sameDecay.First(x => x.W == 1.0m);
        Console.WriteLine("### 权重方向（半衰期固定现状，只动混合比例）");
        Console.WriteLine();
        Console.WriteLine("| 权重 | 说明 | Brier | 相对 0.3 |");
        Console.WriteLine("|---:|---|---:|---:|");
        foreach (var (w, note) in new (decimal W, string Note)[]
                 { (0m, "纯模型"), (0.3m, "现状"), (0.5m, "对半"), (0.7m, "市场为主（你建议）"), (1.0m, "纯市场") })
        {
            var hit = sameDecay.First(x => x.W == w);
            Console.WriteLine($"| {w:0.0} | {note} | {hit.Brier:F4} | {hit.Brier - w03.Brier:+0.0000;-0.0000;0.0000} |");
        }
        Console.WriteLine();
        var dirGain = w03.Brier - w07.Brier;
        Console.WriteLine(dirGain >= MeaningfulGain
            ? $"→ 把权重从 0.3 提到 0.7 改善 {dirGain:+0.0000}，**过门槛，方向该反过来**。"
            : dirGain > 0
                ? $"→ 提到 0.7 只改善 {dirGain:+0.0000}，**未过门槛**：方向对但幅度不足以改配置。"
                : $"→ 提到 0.7 反而变差 {dirGain:+0.0000}：市场并没有更准，现权重不该动。");
        Console.WriteLine();
        Console.WriteLine("> 注意边界：这里测的是**胜平负三向**的混合。半全场/比分/总进球的 EV 依赖同一个");
        Console.WriteLine("> `MarketWeight` 常数，结论可以外推，但幅度不能照搬——那三个玩法的市场概率来自");
        Console.WriteLine("> 固定奖金而非欧赔，去水口径不同。");
        return 0;
    }

    /// <summary>三分类累积器：Brier、LogLoss、样本数。</summary>
    struct Stats
    {
        public int N;
        public double BrierSum;
        public double LogSum;
        public double Brier => N == 0 ? 0 : BrierSum / N;
        public double Log => N == 0 ? 0 : LogSum / N;

        public static Stats Empty() => new();

        public void Add(double ph, double pd, double pa, WRow r)
        {
            var ah = r.HomeScore > r.AwayScore ? 1.0 : 0.0;
            var ad = r.HomeScore == r.AwayScore ? 1.0 : 0.0;
            var aa = r.HomeScore < r.AwayScore ? 1.0 : 0.0;
            BrierSum += Sqr(ph, ah) + Sqr(pd, ad) + Sqr(pa, aa);
            var pActual = Math.Clamp(ah > 0 ? ph : ad > 0 ? pd : pa, 1e-6, 1 - 1e-6);
            LogSum += -Math.Log(pActual);
            N++;
        }

        static double Sqr(double p, double y) => (p - y) * (p - y);
    }

    /// <summary>欧赔去水：1/odds 归一化。</summary>
    static (double H, double D, double A) Fair(decimal h, decimal d, decimal a)
    {
        var ih = 1.0 / (double)h;
        var id = 1.0 / (double)d;
        var ia = 1.0 / (double)a;
        var s = ih + id + ia;
        return s <= 0 ? (1 / 3.0, 1 / 3.0, 1 / 3.0) : (ih / s, id / s, ia / s);
    }

    static double StdDev(List<double> xs)
    {
        if (xs.Count < 2) return 0;
        var mean = xs.Average();
        return Math.Sqrt(xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1));
    }
}
