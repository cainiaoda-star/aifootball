using AiFootBall.Core.Models;

namespace AiFootBall.Sources;

/// <summary>
/// 大奶方向闸（2026-11-05 加）。
///
/// 背景：11-04 晚三场全反（朗斯 1:9 科莫、费内巴切 3:2 利物浦、波尔图没平），
/// 用库里 1873 场已完赛样本回测得到的方向命中率是：
///   亚盘水位 47.7%（≈ 抛硬币）、欧赔隐含 61.0%、必发大额 74.4%、
///   三层一致 84.4%、背离时跟亚盘 26.3%。
/// 所以方向只认市场（竞彩胜平负去水，缺则比分盘反推），模型自己算出来的方向只当参考；
/// 两者背离时落一条诊断（**默认不砍注**，见下方 EnforceDirectionVeto 说明）——否则「模型哪来的自信」这件事赛后无从追查。
///
/// 2026-11-08 修正：构造函数里的 EnforceDirectionVeto 默认 false，即这道闸实际是**诊断闸**，
/// 注照样上榜。原文案写「整场不发」，与真实行为相反，11-08 当天榜上 10 注里 9 注来自 diverge 场次，
/// 每一条诊断都在撒谎。硬砍回测为负 alpha（444 场 3745 注，前四档反向命中优于同向），留注是对的，
/// 错的是那行字——现在 AlertNote 按 EnforceDirectionVeto 的真实取值生成。
///
/// 三条刻意的保守设计：
///   1. 市场快照不完整（档数不够、去水分母跑出合理区间）→ Verdict=unknown，闸不生效。
///      用残缺快照判方向，比不判更危险：档少的时候 1/赔率 会把某一侧放到极大。
///   2. 模型最强方向和市场最强方向都指向同一侧才算 pass；差 3 个点以内当「市场没方向」，
///      边界上不硬拦，免得整天的榜被一个四舍五入清空。
///   3. 亚盘只用来报警，不参与否决。它的历史命中率 47.7%，拿它做方向等于掷硬币，
///      但「亚盘跟市场背离」这个形态本身有信息量（诱盘），记下来给人看。
/// </summary>
public static class MilkDirectionGate
{
    /// <summary>
    /// 模型方向与市场方向的最小分歧幅度（3 个百分点）。
    /// 低于它说明市场自己也没站定，此时不拦——拦了纯属噪声。
    /// </summary>
    public const decimal MinDirectionGap = 0.03m;

    /// <summary>亚盘盘口移动超过 0.20 个盘位才算有方向（0.25 半球是常见的一档）。</summary>
    public const decimal AsianMoveThreshold = 0.20m;

    /// <summary>必发一侧要领先另一侧 50% 以上才算有方向（1.5 倍）。</summary>
    public const decimal ExchangeLead = 1.5m;

    /// <summary>比分盘反推方向所需的最少档数。官方全场比分 28 档，抓一半以上才可信。</summary>
    private const int MinScoreBuckets = 15;

    /// <summary>比分盘的去水分母上限。比分档多，返还率比胜平负低，分母本来就更大。</summary>
    private const decimal MaxScoreOverRound = 3.5m;

    /// <summary>
    /// 给一场比赛做方向诊断。
    /// 模型三项概率传 0-1 的比例（引擎里已经算过，别在这里重算一遍）。
    /// <paramref name="pickDirection"/> 是这场候选注实际指向的方向；整场只有总进球时传 null，
    /// 此时退回模型整体倾向来判断——总进球虽然没方向，但它也是同一套进球期望算出来的，
    /// 方向错了它一样会错。
    /// </summary>
    public static MilkDirectionDiagnosis Judge(
        MatchRecommendation rec,
        IReadOnlyList<JcOddsHistoryPoint> points,
        decimal modelHome,
        decimal modelDraw,
        decimal modelAway,
        (decimal? Open, decimal? Close)? asian = null,
        char? pickDirection = null)
    {
        var diag = new MilkDirectionDiagnosis
        {
            MatchId = rec.MatchId,
            MatchNo = rec.MatchNo,
            LeagueName = rec.LeagueName,
            HomeTeam = rec.HomeTeam,
            AwayTeam = rec.AwayTeam,
            MatchTime = rec.MatchTime,
            ModelHomePct = Math.Round(modelHome * 100m, 2),
            ModelDrawPct = Math.Round(modelDraw * 100m, 2),
            ModelAwayPct = Math.Round(modelAway * 100m, 2),
            PickDirection = pickDirection,
            RatioHome = rec.RatioHome,
            RatioAway = rec.RatioAway
        };

        diag.ModelDirection = Strongest(modelHome, modelDraw, modelAway);

        var market = MarketDirection(points);
        if (market is { } m)
        {
            diag.MarketDirection = m.Dir;
            diag.MarketSource = m.Source;
            diag.MarketHomePct = Math.Round(m.H * 100m, 2);
            diag.MarketDrawPct = Math.Round(m.D * 100m, 2);
            diag.MarketAwayPct = Math.Round(m.A * 100m, 2);
        }

        if (asian is { } a)
        {
            diag.AsianOpen = a.Open;
            diag.AsianClose = a.Close;
            diag.AsianDirection = AsianDirection(a.Open, a.Close);
        }
        diag.ExchangeDirection = ExchangeDirection(rec.RatioHome, rec.RatioAway);

        // 判背离：以「这一注的方向」为准，没方向才退回模型倾向。市场方向必须站到同一侧。
        var judgeDir = pickDirection ?? diag.ModelDirection;

        if (diag.MarketDirection is not char md || judgeDir is not char jd)
        {
            diag.Verdict = MilkGateVerdicts.Unknown;
            diag.AlertNote = diag.MarketDirection is null
                ? "市场方向不可得（快照不完整），方向闸未生效"
                : null;
            return diag;
        }

        if (md == jd)
        {
            diag.Verdict = MilkGateVerdicts.Pass;
        }
        else
        {
            // 分歧幅度：这一注那一侧在市场里值多少 vs 市场最强那一侧值多少。
            var modelSideMarket = MarketProb(diag, jd);
            var marketSideMarket = MarketProb(diag, md);
            var gap = marketSideMarket - modelSideMarket;

            if (gap < MinDirectionGap)
            {
                diag.Verdict = MilkGateVerdicts.Pass;
                diag.AlertNote = $"注的方向{Diag(jd)}与市场{Diag(md)}分歧仅 {gap * 100m:0.0} 个点，视为市场无明确方向";
            }
            else
            {
                diag.Verdict = MilkGateVerdicts.Diverge;
                // 文案必须跟着真实行为走（2026-11-08 修）。原来这里写死「整场不发」，
                // 而 EnforceDirectionVeto=false 时引擎只标注、照常出注——11-08 榜上 10 注里
                // 9 注来自 diverge 场次，每一条诊断都在说「整场不发」，等于系统性对用户撒谎。
                // 硬砍是负 alpha（444 场 3745 注回测：前四档「反向」命中/回报优于「同向」），
                // 所以留注是对的，错的只是这行字。
                diag.AlertNote = $"这一注指{Diag(jd)}、市场指{Diag(md)}"
                               + $"（{diag.MarketSource} 去水：主 {diag.MarketHomePct:0.0}% / 平 {diag.MarketDrawPct:0.0}% / 客 {diag.MarketAwayPct:0.0}%），"
                               + (MilkPickEngine.EnforceDirectionVeto
                                   ? "整场不发"
                                   : $"背离 {gap * 100m:0.0} 个点，照常出注（硬砍是负 alpha，只标注不否决）");
            }
        }

        diag.AlertCode = DetectTrap(diag);
        return diag;
    }

    /// <summary>
    /// 诱盘告警：亚盘或必发与市场方向相反。
    /// 不否决——朗斯那种「亚盘造势、必发真押」的形态本来就该跟必发，市场方向已经包含它了。
    /// </summary>
    private static string? DetectTrap(MilkDirectionDiagnosis d)
    {
        if (d.MarketDirection is not char md) return null;

        var asianTrap = d.AsianDirection is char ad && ad != md;
        var exchangeTrap = d.ExchangeDirection is char ed && ed != md;

        return (asianTrap, exchangeTrap) switch
        {
            (true, true) => MilkGateAlerts.TrapBoth,
            (true, false) => MilkGateAlerts.TrapAsian,
            (false, true) => MilkGateAlerts.TrapExchange,
            _ => null
        };
    }

    /// <summary>
    /// 市场方向。优先竞彩胜平负（三档齐全 + 去水分母合理），
    /// 缺失时用比分盘反推（档数够 + 三个方向都有）。
    /// 两者都不可信就返回 null，闸记 unknown。
    /// </summary>
    public static MarketDirectionResult? MarketDirection(IReadOnlyList<JcOddsHistoryPoint> points)
    {
        // 1) 竞彩胜平负：最直接的一层。
        decimal? h = null, d = null, a = null;
        foreach (var p in points)
        {
            if (p.PlayType != JcPlayTypes.Spf || p.Odds <= 1m) continue;
            switch (p.OptionCode)
            {
                case "3": h = p.Odds; break;
                case "1": d = p.Odds; break;
                case "0": a = p.Odds; break;
            }
        }
        if (h is decimal hh && d is decimal dd && a is decimal aa)
        {
            var over = 1m / hh + 1m / dd + 1m / aa;
            if (over is >= 1.02m and <= 1.80m)
            {
                var (ph, pd, pa) = (1m / hh / over, 1m / dd / over, 1m / aa / over);
                var spfDir = Strongest(ph, pd, pa);
                if (spfDir is not char sd0) return null;
                return new MarketDirectionResult(sd0, ph, pd, pa, "spf");
            }
        }

        // 2) 比分盘反推：把每个比分档的去水概率按胜/平/负加总。
        var buckets = points.Where(p => p.PlayType == JcPlayTypes.Bf && p.Odds > 1m).ToList();
        if (buckets.Count < MinScoreBuckets) return null;

        var o = buckets.Sum(p => 1m / p.Odds);
        if (o is < 1.02m or > MaxScoreOverRound) return null;

        decimal sh = 0m, sd = 0m, sa = 0m;
        foreach (var p in buckets)
        {
            var side = ScoreSide(p.OptionCode);
            if (side is not char s) continue;
            var inv = 1m / p.Odds / o;
            if (s == '3') sh += inv;
            else if (s == '1') sd += inv;
            else sa += inv;
        }
        if (sh <= 0m || sd <= 0m || sa <= 0m) return null;

        var scoreDir = Strongest(sh, sd, sa);
        if (scoreDir is not char sc) return null;
        return new MarketDirectionResult(sc, sh, sd, sa, "score");
    }

    /// <summary>比分档属于哪一侧：3 主胜 / 1 平 / 0 客胜。认不出返回 null。</summary>
    private static char? ScoreSide(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;

        var isOther = code.EndsWith("其它", StringComparison.Ordinal)
                   || code.EndsWith("其他", StringComparison.Ordinal);
        if (isOther)
        {
            return code[0] switch
            {
                '胜' => '3',
                '平' => '1',
                '负' => '0',
                _ => null
            };
        }

        var parts = code.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var a)) return null;
        return h > a ? '3' : h == a ? '1' : '0';
    }

    /// <summary>亚盘方向：主让为负，盘口越走越负 = 更看好主队。</summary>
    public static char? AsianDirection(decimal? open, decimal? close)
    {
        if (open is not decimal o || close is not decimal c) return null;
        var delta = c - o;
        if (delta <= -AsianMoveThreshold) return '3';
        if (delta >= AsianMoveThreshold) return '0';
        return null;
    }

    /// <summary>必发方向：一侧大额领先另一侧 50% 以上才算。</summary>
    public static char? ExchangeDirection(decimal? home, decimal? away)
    {
        if (home is not decimal h || away is not decimal a) return null;
        if (h <= 0m && a <= 0m) return null;
        if (h >= a * ExchangeLead) return '3';
        if (a >= h * ExchangeLead) return '0';
        return null;
    }

    private static char? Strongest(decimal h, decimal d, decimal a)
    {
        if (h <= 0m && d <= 0m && a <= 0m) return null;
        if (h >= d && h >= a) return '3';
        return d >= a ? '1' : '0';
    }

    private static decimal MarketProb(MilkDirectionDiagnosis d, char dir) => dir switch
    {
        '3' => d.MarketHomePct ?? 0m,
        '1' => d.MarketDrawPct ?? 0m,
        _ => d.MarketAwayPct ?? 0m
    } / 100m;

    private static string Diag(char? d) => MilkDirectionDiagnosis.DirName(d);

    /// <summary>市场方向结果。</summary>
    public readonly record struct MarketDirectionResult(char Dir, decimal H, decimal D, decimal A, string Source);
}
