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
    public decimal Odds { get; set; }

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
}

/// <summary>
/// 空跑战绩的一个分组切片。分母统一用「已结算且未撤注」的注数，
/// 撤注单独计数——撤注判得对不对要另算，不能混进命中率。
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
    decimal Roi)
{
    public static PaperSlice Empty(string key) => new(key, 0, 0, 0, 0, 0m, 0m, 0m, 0m);
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
    IReadOnlyList<PaperSlice> ByFlag)
{
    /// <summary>样本够不够下结论。门槛定义在 PaperTradeMath，别在这里另立一个数。</summary>
    public bool Enough => Overall.Settled >= PaperTradeMath.EnoughSettled;
}
