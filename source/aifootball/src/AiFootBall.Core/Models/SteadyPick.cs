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
///   · **每日只取赔率最低前 3 注：77.6% / 0.993**（98 注）；前 2 注 81.8%；前 1 注 90.9%（33 注）。
///   · 命中率随赔率单调下降，回报全区间贴在 0.88~0.95 之间 —— 再次印证「压赔率抬命中、抬不动期望」。
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
    /// 每日最多几注。回测：前 3 注 77.6% / 0.993；前 5 注降到 72.3%。
    /// 3 注给用户「挑一个」的空间，又不会把栏目刷成第二张主榜。
    /// </summary>
    public const int MaxPerDay = 3;

    /// <summary>去水分母的合理区间。超出说明快照残缺（缺档或串价），宁可不出。</summary>
    public const decimal MinOverRound = 1.02m;
    public const decimal MaxOverRound = 1.80m;

    /// <summary>三档都算不出来时，最低可接受的热门程度（去水概率）。低于此值说明这是一场均衡局，不配叫稳胆。</summary>
    public const decimal MinMarketPct = 40m;
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
