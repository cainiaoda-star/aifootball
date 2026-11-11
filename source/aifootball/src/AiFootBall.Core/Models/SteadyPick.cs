namespace AiFootBall.Core.Models;

/// <summary>
/// 稳胆注单（2026-11-09 加，用户拍板「开稳胆栏目」）。
///
/// **与大奶主榜是两条完全不同的产品线，不许混账**：
///   · 大奶主榜（FbMilkPick）= 按 EV 挑价值，命中率被票价锁在 12~15%，目标是期望；
///   · 稳胆（FbSteadyPick）= 按市场最看好档挑「最可能中」，命中率 70%+，目标是**中奖频率**。
/// 两者的长期回报都绕不过返奖率 0.878，所以稳胆不是「更赚钱」，是「更常中」。
/// 用户原话是「一个都没中」，要的是这个；若拿稳胆去当期望来源，就是自欺。
///
/// 口径（<see cref="SteadyPickEngine"/>）：取竞彩**胜平负（不让球）**三档里
/// 去水概率最高的那一档，赔率 ≤ <see cref="SteadyPickRules.MaxOdds"/>，每日按赔率升序取前
/// <see cref="SteadyPickRules.MaxPerDay"/> 注。不掺模型、不掺方向闸、不看必发——
/// 模型四项全打不过市场（DirectionStudy），掺进来只会把命中率往下拉。
///
/// 回测依据（`steady-probe 2026-09-01 1.75`，382 注可出样本、33 个比赛日）：
///   · 全量口径：57.1% 命中 / 回报 0.919；
///   · 赔率 ≤1.50：73.2% / 0.945；≤1.75：67.1% / 0.936；
///   · **每日只取赔率最低前 N 注：前 1 注 91.4% / 回报 1.108（35 注）；前 2 注 81.4% / 1.011；
///     前 3 注 76.9% / 0.983**。
///   · **「压赔率抬不动期望」是错的**（2026-11-10 纠正）：按赔率段切确实各段都贴在
///     0.88~0.95，但按「每日排名」切，前 1 注回报比前 3 注高 12 个点。排名里含着
///     赔率段没有的信息 —— 当天市场最确定的那一场。详见 <see cref="SteadyPickRules.MaxPerDay"/>。
///   · 因此长期回报**可以**高于返奖率，但幅度受限于「每天只有一场够格」，注数天然很少。
/// </summary>
public sealed class SteadyPick
{
    /// <summary>受注日（= 落库的 PickDate）。跨午夜的场次归前一日，与主榜/台账同口径。</summary>
    public DateTime? PickDate { get; set; }
    public long? MatchId { get; set; }
    public string MatchNo { get; set; } = "";
    public string? LeagueName { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }

    /// <summary>稳胆方向：3 主胜 / 1 平 / 0 客胜（竞彩胜平负，不让球）。</summary>
    public char Side { get; set; }

    public string SideName => Side switch
    {
        '3' => "主胜",
        '1' => "平局",
        _ => "客胜"
    };

    /// <summary>该档的竞彩固定奖金（**推荐生成那一刻**的价，不是实购价）。</summary>
    public decimal Odds { get; set; }

    /// <summary>
    /// 终版价（2026-11-10 加）：开赛前最后一次采样，也就是能真正买到的价。
    /// NULL = 还没回填（未开赛，或该场不在采样覆盖内）。来源见 <see cref="FinalPriceSource"/>。
    /// ★ 不改 <see cref="Odds"/>：推荐价是「当时页面上写的」，改了就是篡改历史。
    /// </summary>
    public decimal? FinalOdds { get; set; }

    /// <summary>终版价对应的采样时间。距开赛越近越可信。</summary>
    public DateTime? FinalAt { get; set; }

    /// <summary>终版价来源：jc-close / bd-resultsp。NULL = 没有终版价。</summary>
    public string? FinalSource { get; set; }

    /// <summary>市场去水后的概率（%）。这是「为什么选它」的唯一依据——它就是市场最看好的那一档。</summary>
    public decimal MarketPct { get; set; }

    /// <summary>当日排序：1 = 当日首选（赔率最低）。前端按它排，也是「想只打一注就打这个」的依据。</summary>
    public int Rank { get; set; }

    /// <summary>可选备注（例如「三档赔率都很接近，没有明显热门」）。</summary>
    public string? Note { get; set; }

    /// <summary>实际比分，形如 "2:1"。结算后写入。</summary>
    public string? ActualScore { get; set; }

    /// <summary>是否命中。NULL = 未结算。</summary>
    public bool? Hit { get; set; }

    public DateTime? SettledAt { get; set; }
}

/// <summary>某一天的稳胆 + 近 30 天战绩（页面一次请求就够）。</summary>
public sealed class SteadyPickDay
{
    public DateTime Day { get; set; }
    public List<SteadyPick> Picks { get; set; } = new();
    public SteadyStats Stats { get; set; } = new();
}

/// <summary>
/// 稳胆战绩。口径与主榜台账一致：分母 = 已结算注数，回报 = 命中赔率合计 ÷ 注数。
///
/// **两栏（2026-11-10 加）**：<see cref="Roi"/> 用推荐价、<see cref="RoiFinal"/> 用终版价。
/// 稳胆赔率只有 1.0~1.75，回报贴着 0.95~1.00 走，3% 的价差就能让结论从「打平」变「亏 5%」，
/// 所以跟单评价一律看 RoiFinal；Roi 只用于和历史数字衔接。
/// </summary>
public sealed class SteadyStats
{
    public int Days { get; set; }
    public int Settled { get; set; }
    public int Hits { get; set; }
    public int Pending { get; set; }
    public decimal? AvgOdds { get; set; }
    public decimal HitPct { get; set; }

    /// <summary>推荐价口径的回报（历史口径，保留）。</summary>
    public decimal Roi { get; set; }

    /// <summary>终版价口径的回报 = 命中注终版价合计 ÷ 已结算注数。跟单评价看这个。</summary>
    public decimal RoiFinal { get; set; }

    /// <summary>命中注里拿得到终版价的注数。</summary>
    public int FinalHits { get; set; }

    /// <summary>终版价覆盖率（%，分母 = 命中注数）。低于 95 时 RoiFinal 会低估，只能参考。</summary>
    public decimal FinalCoverage { get; set; }

    /// <summary>终版价口径能不能当账本（覆盖率 ≥95%）。</summary>
    public bool FinalVerified => FinalPriceMath.Trustworthy(Hits, FinalHits);
}

/// <summary>
/// 稳胆口径常量。**集中在这里，引擎与结算共用一份**——
/// 本项目已经因为「两道闸各写一份常数」踩过坑（出票放行后哨兵立刻撤注）。
/// </summary>
public static class SteadyPickRules
{
    /// <summary>
    /// 赔率上限。回测：≤1.75 命中 67.1%（222 注、回报 0.936）；
    /// 放宽到 3.0 命中率降到 57.1%，命中率换不来回报（回报 0.919 反而更低）。
    /// 卡在 1.75 是「命中率还很高、注数还够挑」的交点。
    /// </summary>
    public const decimal MaxOdds = 1.75m;

    /// <summary>
    /// 每日最多几注。**2026-11-11 由 2 改 1**。
    ///
    /// 改前只记了命中率、没记**回报**，于是得出了「压赔率抬命中、抬不动期望」的结论。
    /// 补上回报之后这个结论是错的，而且是反的 —— 按「每日排名」切，回报随注数单调下降：
    ///
    ///   前 1 注 35 注 91.4% 命中 / **回报 1.108**
    ///   前 2 注 70 注 81.4% / **1.011**
    ///   前 3 注 104 注 76.9% / 0.983
    ///   （`steady-probe` 与一条独立 SQL 两套实现算出来一致，误差 &lt;0.01）
    ///
    /// 为什么「按赔率段切」看不出这件事（各段都在 0.88~0.95）、「按每日排名切」却差 12 个点：
    /// 排名里含着赔率段没有的信息 —— **当天市场最确定的那一场**。买 1.5 的注很多，
    /// 但「今天所有场次里最被看好的一注」只有一注。
    ///
    /// ★ 2026-11-11 改 2→1 的推算：前 2 注 1.011 减去前 1 注 1.108，反解出
    ///   **Rank=2 那注单独看只有 0.914**（(70×1.011−35×1.108)/35 = 0.914）——
    ///   它自己是亏的。昨天写「取 2 是为了样本攒快一倍」，等于拿一注确定亏损的票
    ///   去摊薄唯一正向的那注，本末倒置。要样本可以另开影子台账，不该混进出票。
    ///
    /// 代价必须写清楚：注数从每天 2 降到 1，样本积累速度减半，
    /// 而 35 注本身 1σ 区间约 [0.91, 1.30] —— **下限在 1 以下，1.108 不是确定盈利**。
    /// 所以这不是「调参优化」，是「只在唯一有证据的方向上下注，其余一律不下」。
    /// </summary>
    public const int MaxPerDay = 1;

    /// <summary>去水分母的合理区间。超出说明快照残缺（缺档或串价），宁可不出。</summary>
    public const decimal MinOverRound = 1.02m;
    public const decimal MaxOverRound = 1.80m;

    /// <summary>三档都算不出来时，最低可接受的热门程度（去水概率）。低于此值说明这是一场均衡局，不配叫稳胆。</summary>
    public const decimal MinMarketPct = 40m;

    /// <summary>
    /// 竞彩受注日：由场次号前缀「周x」判定，**不看开赛日期**。
    ///
    /// 2026-11-10 修：作业原来一律用 <c>DateTime.Today</c> 落库。20:50 那轮页面已经
    /// 切到次日期号（实测 22:06 预览拿到「周三005 11-12 03:00」「周三006」），
    /// 存进去就把次日的注记成了今天 —— 受注日错位一天，台账、结算、复盘全跟着错。
    /// 更糟的是 <c>SaveDayAsync</c> 会先删「当天未结算且未开赛」的行，
    /// 于是同一批行先被删再被另一期的注覆盖，榜面看起来在跳，其实是两个期在抢一个日期。
    ///
    /// 口径与 <c>HalfDrawRepository</c> / <c>MilkPickRepository</c> 一致：期号说了算。
    /// 「周三005」开球在 11-12 03:00，属于 **11-11** 那一期，不是 11-10、也不是 11-12。
    ///
    /// 解析不出前缀（北单纯数字场次号）时退回开赛时间规则：12:00 前开球归前一日，
    /// 这正是判据库里「受注日 ≠ 开赛日」那条。
    /// </summary>
    public static DateTime SalesDate(string? matchNo, DateTime? matchTime, DateTime fallback)
    {
        if (matchTime is not DateTime mt) return fallback.Date;

        var want = WeekdayOfMatchNo(matchNo);
        if (want == 0)
            return mt.Hour < 12 ? mt.Date.AddDays(-1) : mt.Date;

        // 从开赛日往回找最近的那个星期几。找满 7 天说明数据坏了，交给 fallback。
        var d = mt.Date;
        for (var i = 0; i < 7; i++, d = d.AddDays(-1))
            if (WeekdayNumber(d.DayOfWeek) == want) return d;

        return fallback.Date;
    }

    /// <summary>1=周一 … 7=周日。和场次号前缀同一套编号。</summary>
    public static int WeekdayNumber(DayOfWeek w) => w == DayOfWeek.Sunday ? 7 : (int)w;

    /// <summary>场次号前缀 → 1=周一 … 7=周日；解析不出返回 0。</summary>
    public static int WeekdayOfMatchNo(string? matchNo)
    {
        if (string.IsNullOrWhiteSpace(matchNo)) return 0;
        var s = matchNo;
        if (s.Contains("一", StringComparison.Ordinal)) return 1;
        if (s.Contains("二", StringComparison.Ordinal)) return 2;
        if (s.Contains("三", StringComparison.Ordinal)) return 3;
        if (s.Contains("四", StringComparison.Ordinal)) return 4;
        if (s.Contains("五", StringComparison.Ordinal)) return 5;
        if (s.Contains("六", StringComparison.Ordinal)) return 6;
        if (s.Contains("日", StringComparison.Ordinal)) return 7;
        return 0;
    }
}

/// <summary>
/// 稳胆结算：纯函数，可单测。
///
/// 口径就是竞彩胜平负：主队进球多于客队 = 3，相等 = 1，少于 = 0。
/// 不涉及让球——稳胆走的是 SPF（不让球）三档，这一点和北单/让球玩法不同，
/// 混用会让结算全盘错（北单那份是「净胜 + 让球数」，见 BdSpfSettlement）。
/// </summary>
public static class SteadySettlement
{
    /// <summary>实际结果代码：3 主胜 / 1 平 / 0 客胜。</summary>
    public static char ResultOf(int homeScore, int awayScore)
        => homeScore > awayScore ? '3' : homeScore == awayScore ? '1' : '0';

    /// <summary>这一注中没中。比分缺失返回 null（未结算，不许当没中）。</summary>
    public static bool? Judge(char side, int? homeScore, int? awayScore)
    {
        if (homeScore is not int h || awayScore is not int a) return null;
        return ResultOf(h, a) == side;
    }

    /// <summary>比分的展示形式 "2:1"。</summary>
    public static string ScoreText(int homeScore, int awayScore) => $"{homeScore}:{awayScore}";
}
