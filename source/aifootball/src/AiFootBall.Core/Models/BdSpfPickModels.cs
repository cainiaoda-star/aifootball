namespace AiFootBall.Core.Models;

/// <summary>北单胜平负（不让球）推荐一注（只读展示 + 落库，2026-11-06 加）。</summary>
public sealed class BdSpfPick
{
    public long MatchId { get; set; }
    public string MatchNo { get; set; } = "";
    public string? LeagueName { get; set; }
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public DateTime? MatchTime { get; set; }
    /// <summary>3=主胜 1=平 0=客胜（不让球胜平负）。</summary>
    public char Side { get; set; }
    public string SideName { get; set; } = "";
    public decimal? Odds { get; set; }
    /// <summary>让球数（北单 rangqiuNum，主队让球为正负，如 -1 表示主队让1球）。</summary>
    public decimal? Handicap { get; set; }
    /// <summary>让球盘方向：3=让胜 1=让平 0=让负（北单真实玩法，按让球数结算）。</summary>
    public char? HandicapSide { get; set; }
    public string? HandicapSideName { get; set; }
    /// <summary>让球盘该方向打出的概率（0~100，引擎算的）。</summary>
    public decimal? HandicapProbability { get; set; }

    /// <summary>
    /// 模型自己算的该让球方向概率（0~100）。
    ///
    /// ★ 2026-11-11 补落库。此前这个字段只在内存里算过、从没写进库，导致一个致命问题：
    /// 推荐方向是按「市场概率最大的一档」选的（RecommendationSelection.ApplyJcHandicap），
    /// 于是 HandicapProbability ≡ 市场概率，EV = p×odds ≡ 1/Σ(1/SP) ≈ 1.00 —— 数学上没有 edge，
    /// 系统一直在抄庄家的答案。实测 55 注 avg(p×odds)=0.9834、北单在售 6 场 Σ(1/SP)=1.0009~1.0034。
    /// 要和市场比出胜负，必须留下模型自己的概率。
    /// </summary>
    public decimal? HandicapModelProb { get; set; }

    /// <summary>市场（盘口去水后）的该让球方向概率（0~100）。与 HandicapModelProb 配对才能回答「谁更准」。</summary>
    public decimal? HandicapMarketProb { get; set; }
    /// <summary>让球盘 SP 赔率（**推荐生成那一刻**的价，开奖前会变，不是实购价）。</summary>
    public decimal? HandicapOdds { get; set; }

    /// <summary>
    /// 终版价（2026-11-10 加）：命中的注取开奖 SP（FbBdHistory.ResultSp），
    /// 未命中的注没有终版价——没开出的选项本来就没有 SP。
    /// 详情见 <see cref="FinalPriceSource"/>。
    /// </summary>
    public decimal? FinalOdds { get; set; }

    /// <summary>终版价对应的时刻（开奖采集时间）。</summary>
    public DateTime? FinalAt { get; set; }

    /// <summary>终版价来源：bd-resultsp。NULL = 没有终版价。</summary>
    public string? FinalSource { get; set; }
    public decimal? HitProbability { get; set; }
    public decimal? MarketProb { get; set; }
    public decimal? RatioHome { get; set; }
    public decimal? RatioAway { get; set; }
    public decimal? PnlIdxHome { get; set; }
    public decimal? PnlIdxAway { get; set; }
    /// <summary>steady=串关正路（主/客真碾压）；reverse=反向爆冷参考（不进串关）。</summary>
    public string Kind { get; set; } = "steady";
    /// <summary>把握度分（0~100，串关排序用，越高越稳）。</summary>
    public int Confidence { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>
/// 当天北单胜平负推荐（2026-11-06 调整为「串关」用途）：
///   · Steady = 串关正路（主队/客队真碾压，宁缺毋滥取前 10，用于 10串1/9串1/8串1）；
///   · Reverse = 反向爆冷参考（钱往客胜跑，单独展示，不参与串关）。
/// </summary>
public sealed class BdSpfPickDay
{
    public DateTime Day { get; set; }
    public List<BdSpfPick> Steady { get; set; } = new();
    public List<BdSpfPick> Reverse { get; set; } = new();
}

/// <summary>北单战绩分组（2026-11-09 加，配套 bd-spf-settle）。</summary>
public sealed class BdSpfRecordGroup
{
    public string Kind { get; set; } = "total";
    /// <summary>已结算注数（分母）。未结算的不进分母。</summary>
    public int Settled { get; set; }
    public int Hits { get; set; }
    /// <summary>还没结算的注数（今晚待开赛 + 缺赛果）。</summary>
    public int Pending { get; set; }
    public decimal HitPct { get; set; }

    /// <summary>推荐价口径的平注回报（= 命中注推荐价比合计 ÷ 注数）。历史口径，保留。</summary>
    public decimal Roi { get; set; }
    public decimal? AvgOdds { get; set; }

    /// <summary>
    /// 开奖SP口径的回报（2026-11-10 加）= 命中注开奖SP合计 ÷ 已结算注数。
    /// **这是真实赔付**：北单按 SP 派奖，中多少付多少，不是「假设按推荐价买到」。
    /// </summary>
    public decimal RoiFinal { get; set; }

    /// <summary>命中注里拿得到开奖 SP 的注数。</summary>
    public int FinalHits { get; set; }

    /// <summary>开奖SP覆盖率（%，分母 = 命中注数）。北单理论上应接近 100，低了说明开奖期次没采到。</summary>
    public decimal FinalCoverage { get; set; }

    public bool FinalVerified => FinalPriceMath.Trustworthy(Hits, FinalHits);
}

/// <summary>
/// 北单战绩（近 N 天）。★ 现在有两栏：<c>Roi</c> 是推荐时点 SP、<c>RoiFinal</c> 是开奖 SP。
/// 跟单能不能赚只看 RoiFinal——那是真实赔付价；Roi 只用来和历史数字衔接。
/// </summary>
public sealed class BdSpfRecord
{
    public int Days { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public BdSpfRecordGroup Total { get; set; } = new();
    public BdSpfRecordGroup? Steady { get; set; }
    public BdSpfRecordGroup? Reverse { get; set; }
    public List<BdSpfPickRecordItem> Rows { get; set; } = new();
    public string Note { get; set; } =
        "Roi = 推荐时点 SP 口径，RoiFinal = 开奖 SP 口径（真实赔付）。跟单看 RoiFinal；" +
        "开奖 SP 只有命中的注才有（未开出的选项没有 SP），所以覆盖率按命中注算。";
}

/// <summary>北单战绩逐注行（前端表格用）。</summary>
public sealed class BdSpfPickRecordItem
{
    public DateTime PickDate { get; set; }
    public string Kind { get; set; } = "steady";
    public string MatchNo { get; set; } = "";
    public string? LeagueName { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }
    public decimal? Handicap { get; set; }
    public string? SideName { get; set; }
    public decimal? Odds { get; set; }
    /// <summary>开奖 SP（终版价）。只有命中的注有值。</summary>
    public decimal? FinalOdds { get; set; }
    public string? ActualScore { get; set; }
    public bool? Hit { get; set; }
}
