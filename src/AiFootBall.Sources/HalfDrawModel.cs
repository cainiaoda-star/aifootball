namespace AiFootBall.Sources;

/// <summary>
/// 半全场「胜平 3-1 / 负平 0-1」提取模型（2026-11-08 建，脚本原型 tools/halfdraw.js）。
///
/// 为什么需要单独一个模型：竞彩对这两档**报同一个价**（今日 live 页面 30/30 同价，
/// 库内 552/552 差值为 0；曼城主胜 1.10 的场次胜胜 1.38、负负 23.00，但胜平=负平=28.00）。
/// 也就是说价格里完全不含方向信息——「半场领先被追平」和「半场落后追平」被当成一样的概率。
/// 要判断这个价贵不贵，只能自己算方向。
///
/// 拆解（两步）：
///     P(胜平) = Σn P(全场 n:n) × P(半场主队领先 | 全场 n:n)
///     P(负平) = Σn P(全场 n:n) × P(半场客队领先 | 全场 n:n)
///
/// · P(全场 n:n)：用竞彩**总进球(JQ)报价**定总额分布，再按主客进球占比二项拆分。
///   比独立泊松准——它直接采信市场对总进球的信息。JQ 缺档时回落泊松。
/// · 方向率 q(n)：2023-2025 主流联赛 10340 场真实平局实测（见 <see cref="QHomeLead"/>）。
///
/// 校准结果（495 场竞彩完场）：模型 P(胜平) 5.06%、市场隐含 5.07%、可信窗口实际 4.90%；
/// P(全场平) 模型 23.6%、实际 24.0%。
///
/// ⚠ 结论同样是模型的一部分：**这一档没有正期望**。495 场平均 EV(胜平)=0.796（≈返奖率本身）、
/// EV(负平)=0.740，EV≥1.0 只出现 1 场、EV≥1.1 为 0；按总进球、赔率、实力差三个维度分档全是平的。
/// 所以本模型**只做公平价参考，不做选注通道**——别把它接成推荐。
///
/// ⚠ 数据前提：半场比分在 FbMatch 里 2026-10-21 之前是 100% 复制全场比分（历史导入没带半场），
/// 且小联赛（西协丙/德地区/意丙1A）至今仍是复制。任何"近一个月"的半全场验证必须只用 10-21 之后的窗口，
/// 否则反转档会被脏数据稀释成 0.7% 这种假信号。本模型的 q(n) 取自 2023-2025 长历史，不受影响。
/// </summary>
public static class HalfDrawModel
{
    /// <summary>竞彩半全场返奖率（九档倒数和实测 1.2545 → 0.7971）。</summary>
    public const decimal Payback = 0.797m;

    private const int MaxGoal = 10;      // 单队进球截断（泊松尾部可忽略）
    private const double Grid = 0.05;    // λ 网格步长
    private const int N = 100;           // λ 上限 = N × Grid = 5.0
    private const int W = N + 1;         // 一维平铺宽度（下标 0 不用）

    /// <summary>
    /// 平局各比分下「半场主队领先」的实测占比（下标 = 比分 n:n 的 n）。
    /// 来源：2026-11-08 重算，FbMatch 2024-08 起真半场（半场 ≤ 全场），平局 42,384 场。
    /// n=0（0:0）恒为 0——半场必平，两档都不可能中。n=1:23608 / 2:10494 / 3:2291 / 4:285。
    /// 旧值（0.2563/0.3373/0.3573/0.3600）是用「半场=全场」的副本数据算的，已作废。
    /// </summary>
    private static readonly double[] QHomeLead = { 0d, 0.2527, 0.3134, 0.3400, 0.3719 };

    /// <summary>同上，「半场客队领先」。两侧不对称只有 1~5 个点（比旧表算出的「约 8%」更小）。</summary>
    private static readonly double[] QAwayLead = { 0d, 0.2414, 0.3158, 0.3326, 0.3228 };

    // 网格预算：SPF 三项概率 → (λh, λa) 的最小二乘查找表。
    private static readonly double[] PW = new double[W * W];
    private static readonly double[] PD = new double[W * W];
    private static readonly double[] PL = new double[W * W];

    static HalfDrawModel() => Build();

    private static double[] Poisson(double lam)
    {
        var p = new double[MaxGoal + 1];
        p[0] = Math.Exp(-lam);
        for (int k = 1; k <= MaxGoal; k++) p[k] = p[k - 1] * lam / k;
        return p;
    }

    private static void Build()
    {
        var pmf = new double[N + 1][];
        for (int i = 1; i <= N; i++) pmf[i] = Poisson(i * Grid);

        for (int i = 1; i <= N; i++)
        {
            var ph = pmf[i];
            int rowBase = i * W;
            for (int j = 1; j <= N; j++)
            {
                var pa = pmf[j];
                double pw = 0, pd = 0, pl = 0;
                for (int a = 0; a <= MaxGoal; a++)
                {
                    double x = ph[a];
                    if (x <= 0) continue;
                    for (int b = 0; b <= MaxGoal; b++)
                    {
                        double v = x * pa[b];
                        if (a > b) pw += v;
                        else if (a == b) pd += v;
                        else pl += v;
                    }
                }
                int k = rowBase + j;
                PW[k] = pw; PD[k] = pd; PL[k] = pl;
            }
        }
    }

    /// <summary>
    /// 主入口。SPF 三项缺任一则返回 null（没有主客强弱，模型无从下手）。
    /// <paramref name="totalGoals"/> 为竞彩总进球 0~6、7+ 八档赔率，缺档或含非法值时回落泊松。
    /// </summary>
    public static HalfDrawEstimate? Estimate(
        decimal? o3, decimal? o1, decimal? o0,
        decimal?[]? totalGoals,
        decimal? o31, decimal? o01)
    {
        if (o3 is not > 1m || o1 is not > 1m || o0 is not > 1m) return null;

        // 1) SPF 归一化（去掉庄家水位）→ 网格最小二乘反解 λh / λa
        double i3 = 1d / (double)o3.Value;
        double i1 = 1d / (double)o1.Value;
        double i0 = 1d / (double)o0.Value;
        double t = i3 + i1 + i0;
        double m3 = i3 / t, m1 = i1 / t, m0 = i0 / t;

        double best = double.MaxValue;
        int bestK = 0;
        for (int i = 1; i <= N; i++)
        {
            int rowBase = i * W;
            for (int j = 1; j <= N; j++)
            {
                int k = rowBase + j;
                double d = (PW[k] - m3) * (PW[k] - m3)
                         + (PD[k] - m1) * (PD[k] - m1)
                         + (PL[k] - m0) * (PL[k] - m0);
                if (d < best) { best = d; bestK = k; }
            }
        }

        double lh = (bestK / W) * Grid;
        double la = (bestK % W) * Grid;
        double pHomeShare = lh / (lh + la);

        // 2) 全场总进球分布（0..6、7+）：优先采信竞彩 JQ 报价
        var tot = new double[8];
        bool usedJq = false;
        if (totalGoals is { Length: >= 8 } && totalGoals.Take(8).All(v => v is > 1m))
        {
            double s = 0;
            var inv = new double[8];
            for (int k = 0; k < 8; k++) { inv[k] = 1d / (double)totalGoals[k]!.Value; s += inv[k]; }
            for (int k = 0; k < 8; k++) tot[k] = inv[k] / s;
            usedJq = true;
        }
        else
        {
            var ph = Poisson(lh);
            var pa = Poisson(la);
            for (int a = 0; a <= MaxGoal; a++)
                for (int b = 0; b <= MaxGoal; b++)
                    tot[Math.Min(a + b, 7)] += ph[a] * pa[b];
        }

        // 3) P(全场 n:n) = P(总进球 2n) × C(2n,n) × p^n × (1-p)^n
        var ftNN = new double[5];
        for (int n = 0; n <= 4; n++)
        {
            int k = 2 * n;
            double pt = k <= 6 ? tot[k] : tot[7];
            ftNN[n] = pt * Binom(2 * n, n) * Math.Pow(pHomeShare, n) * Math.Pow(1 - pHomeShare, n);
        }

        double pDraw = 0, p31 = 0, p01 = 0;
        for (int n = 0; n <= 4; n++) pDraw += ftNN[n];
        for (int n = 1; n <= 4; n++)
        {
            p31 += ftNN[n] * QHomeLead[n];
            p01 += ftNN[n] * QAwayLead[n];
        }

        return new HalfDrawEstimate
        {
            LambdaHome = Math.Round((decimal)lh, 2),
            LambdaAway = Math.Round((decimal)la, 2),
            DrawProbability = Math.Round((decimal)pDraw * 100m, 2),
            ProbWinDraw = Math.Round((decimal)p31 * 100m, 2),
            ProbLoseDraw = Math.Round((decimal)p01 * 100m, 2),
            UsedTotalGoalsOdds = usedJq,
            OddsWinDraw = o31,
            OddsLoseDraw = o01,
            ImpliedWinDraw = o31 is > 1m ? Math.Round(Payback / o31.Value * 100m, 2) : null,
            EvWinDraw = o31 is > 1m ? Math.Round((decimal)p31 * o31.Value, 3) : null,
            EvLoseDraw = o01 is > 1m ? Math.Round((decimal)p01 * o01.Value, 3) : null,
            FairOddsWinDraw = p31 > 0 ? Math.Round(1m / (decimal)p31, 2) : null,
            FairOddsLoseDraw = p01 > 0 ? Math.Round(1m / (decimal)p01, 2) : null
        };
    }

    private static double Binom(int n, int k)
    {
        double r = 1;
        for (int i = 0; i < k; i++) r = r * (n - i) / (i + 1);
        return r;
    }
}

/// <summary>一场比赛的胜平/负平模型输出（概率为百分数）。</summary>
public sealed class HalfDrawEstimate
{
    public decimal LambdaHome { get; init; }
    public decimal LambdaAway { get; init; }

    /// <summary>P(全场平)，百分数。</summary>
    public decimal DrawProbability { get; init; }

    /// <summary>P(胜平) = P(半场主队领先 且 全场平)，百分数。</summary>
    public decimal ProbWinDraw { get; init; }

    /// <summary>P(负平) = P(半场客队领先 且 全场平)，百分数。</summary>
    public decimal ProbLoseDraw { get; init; }

    /// <summary>总进球分布是否采信了竞彩 JQ 报价；false 表示回落泊松。</summary>
    public bool UsedTotalGoalsOdds { get; init; }

    public decimal? OddsWinDraw { get; init; }
    public decimal? OddsLoseDraw { get; init; }

    /// <summary>市场对该价隐含的概率 = 返奖率 ÷ 赔率，百分数。与 <see cref="ProbWinDraw"/> 直接可比。</summary>
    public decimal? ImpliedWinDraw { get; init; }

    public decimal? EvWinDraw { get; init; }
    public decimal? EvLoseDraw { get; init; }

    public decimal? FairOddsWinDraw { get; init; }
    public decimal? FairOddsLoseDraw { get; init; }

    /// <summary>
    /// 胜平的价格判定。分级只用来告诉用户「这个价相对公平价贵多少」，
    /// **不是买入信号**——实测 EV 全在 0.72~0.81，这一档长期负期望。
    /// </summary>
    public string VerdictWinDraw => EvWinDraw switch
    {
        null => "无报价",
        >= 1.02m => "模型认为偏便宜（罕见）",
        >= 0.95m => "接近公平价",
        >= 0.85m => "略贵",
        _ => "明显偏贵"
    };

    /// <summary>
    /// 负平判定：恒定「永不购买」。市场把胜平的价格套用到负平上，而负平真实概率比胜平低约 8%
    /// （10.2% 对 11.1% 的方向差），实测平均 EV 0.740，比胜平再低 7 个点。这是结构性高估，不是概率问题。
    /// </summary>
    public string VerdictLoseDraw => OddsLoseDraw is null ? "无报价" : "系统性高估，永不购买";
}
