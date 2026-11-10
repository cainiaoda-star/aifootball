namespace AiFootBall.Core.Models;

/// <summary>
/// 空跑台账一行：某个比赛日「终版榜」上的一注，冻结后除结算外不再改。
///
/// 为什么要单独一张表、而不直接查 FbMilkPick：
/// ① `SaveDayAsync` 每次重算都 `DELETE ... WHERE Hit IS NULL AND SettledAt IS NULL`，
///    盘中 08:40 / 11:40 / 16:40 / 20:40 / 临场 T-60 / 停售前终版 每一版都会把上一版冲掉。
///    真正能代表「这天系统让我买什么」的只有最后一版，而它在被下次覆盖前没有任何留痕。
/// ② 台账一旦被引擎改动（加闸、改基线、换口径），事后拿 FbMilkPick 回看，
///    分不清「当时就是这么发的」和「用新代码重算出来的」。冻结表让两周后的评价可比。
/// ③ 撤注（Voided）的行在 FbMilkPick 里仍是未结算行，下一次重算直接删掉，
///    撤注判得对不对这条线索就永久丢了。
///
/// 赔率一经冻结不再改：那是「当时照着买会拿到什么价」。只有撤注标记允许事后同步。
/// </summary>
public sealed class PaperTradeRow
{
    public long TradeId { get; set; }
    public DateTime TradeDate { get; set; }
    public long MatchId { get; set; }
    public string MatchNo { get; set; } = string.Empty;
    public string? LeagueName { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }

    public string PlayType { get; set; } = string.Empty;
    public string OptionCode { get; set; } = string.Empty;
    public string OptionName { get; set; } = string.Empty;

    /// <summary>冻结那一刻榜面上的价（= 上场前的价，不是停售价）。</summary>
    public decimal Odds { get; set; }

    /// <summary>
    /// 终版价（2026-11-10 加）：开赛前最后一次采样。NULL = 还没回填。
    /// ★ 不改 <see cref="Odds"/>：台账只增不改，冻结价是「当时榜面写的价」。
    /// </summary>
    public decimal? FinalOdds { get; set; }
    public DateTime? FinalAt { get; set; }
    public string? FinalSource { get; set; }

    /// <summary>落库时是 Blend 值（0.7 模型 + 0.3 市场），不是纯模型。</summary>
    public decimal? ModelProbability { get; set; }
    public decimal? BaselineProbability { get; set; }
    public decimal? BaselineEv { get; set; }
    public decimal? ModelEv { get; set; }

    public bool CounterHigh { get; set; }
    public bool ModelOverconfident { get; set; }
    public bool ModelLowConfidence { get; set; }

    /// <summary>撤注。撤了就当作没下这注，不进回报分母。</summary>
    public bool Voided { get; set; }
    public string? AlertSeverity { get; set; }
    public string? AlertReason { get; set; }

    public DateTime FrozenAt { get; set; }

    public string? ActualScore { get; set; }
    public string? ActualHalfScore { get; set; }
    public bool? Hit { get; set; }
    public DateTime? SettledAt { get; set; }

    /// <summary>平注盈亏：命中 = 赔率 − 1，未中 = −1，撤注 = NULL（没下注）。</summary>
    public decimal? Pnl { get; set; }

    /// <summary>
    /// 换档时被后一版榜面换掉的旧档（只在读取时算，不落库）。
    /// 它进不了任何统计，只用于明细页标注「这行不算注」。
    /// </summary>
    public bool Superseded { get; set; }
}

/// <summary>
/// 空跑战绩的一个分组切片。分母统一用「已结算且未撤注」的注数，
/// 撤注单独计数——撤注判得对不对要另算，不能混进命中率。
///
/// 后三个参数是终版价口径（2026-11-10 加）：<see cref="Roi"/> 用冻结价，
/// <see cref="RoiFinal"/> 用停售前最后采样。跟单评价看 RoiFinal。
///
/// 再后两个是撤注对照组（2026-11-10 加）：<see cref="VoidedSettled"/> /
/// <see cref="VoidedHits"/> 回答的是「撤掉的那些注，如果没撤会怎样」。
/// 撤注判据一直无法回测，卡的就是这个数从来没有被算出来过——
/// 撤注行照常结算、Hit 照常写，但报表把它们整个排除在分母外，
/// 于是「撤对了没有」这个问题在页面上没有任何答案。
/// </summary>
public sealed record PaperSlice(
    string Key,
    int Total,
    int Voided,
    int Settled,
    int Hits,
    decimal HitPct,
    decimal Stake,
    decimal Return,
    decimal Roi,
    int FinalHits = 0,
    decimal ReturnFinal = 0m,
    decimal RoiFinal = 0m,
    int VoidedSettled = 0,
    int VoidedHits = 0)
{
    /// <summary>撤注对照命中率（%）。分母 = 已撤注但有赛果的注数，样本不足时不要当结论。</summary>
    public decimal VoidedHitPct => VoidedSettled == 0
        ? 0m
        : Math.Round(VoidedHits * 100m / VoidedSettled, 2);
    public static PaperSlice Empty(string key) => new(key, 0, 0, 0, 0, 0m, 0m, 0m, 0m);

    /// <summary>终版价覆盖率（%，分母 = 命中注数）。</summary>
    public decimal FinalCoverage => FinalPriceMath.HitCoverage(Hits, FinalHits);

    /// <summary>RoiFinal 能不能当账本。</summary>
    public bool FinalVerified => FinalPriceMath.Trustworthy(Hits, FinalHits);
}

/// <summary>空跑战绩报表：整体 + 按玩法 + 按日 + 按标记。</summary>
public sealed record PaperReport(
    int Days,
    DateTime FromDate,
    DateTime ToDate,
    int FrozenDays,
    PaperSlice Overall,
    IReadOnlyList<PaperSlice> ByPlayType,
    IReadOnlyList<PaperSlice> ByDate,
    IReadOnlyList<PaperSlice> ByFlag,
    /// <summary>换档被换掉的旧档数。它们不在任何切片里，只在报表上标出来好对账。</summary>
    int Superseded = 0)
{
    /// <summary>样本够不够下结论。门槛定义在 PaperTradeMath，别在这里另立一个数。</summary>
    public bool Enough => Overall.Settled >= PaperTradeMath.EnoughSettled;
}
