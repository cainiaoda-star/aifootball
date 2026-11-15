using System.Text.Json.Serialization;

namespace AiFootBall.Core.Models;

/// <summary>
/// 一条否决记录：某天某场本来有方向，被某条规则挡下没发出去。
/// 只做旁路观测——不参与发布、不参与命中率、不参与校准。
/// </summary>
public sealed class VetoRecord
{
    public long VetoId { get; set; }
    public string Board { get; set; } = "";
    public DateTime RecDate { get; set; }
    public long? MatchId { get; set; }
    public string MatchNo { get; set; } = "";
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }

    /// <summary>被挡掉的方向：3 主胜 / 1 平 / 0 客胜。</summary>
    public char? Pick { get; set; }
    /// <summary>被挡掉的让球方向，北单用。</summary>
    public char? HcpPick { get; set; }
    public decimal? Handicap { get; set; }

    /// <summary>机器可读的否决原因代码。</summary>
    public string ReasonCode { get; set; } = "";
    /// <summary>当时的信号描述原文。</summary>
    public string? Reason { get; set; }

    public int? SignalLevel { get; set; }
    public decimal? HitProbability { get; set; }
    public decimal? HcpProbability { get; set; }
    /// <summary>该方向当时的水位。回报率靠它算，缺了就只能看命中率。</summary>
    public decimal? PickOdds { get; set; }
    public decimal? MarketProb { get; set; }
    public decimal? RatioHome { get; set; }
    public decimal? RatioAway { get; set; }
    public decimal? OddsDelta { get; set; }

    public char? ActualResult { get; set; }
    public string? ActualScore { get; set; }
    /// <summary>若当时真按这个方向买，中没中。</summary>
    public bool? Hit { get; set; }
    public bool? HcpHit { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>按否决原因聚合的统计。</summary>
public sealed class VetoStatRow
{
    public string ReasonCode { get; set; } = "";
    /// <summary>累计被挡场次。</summary>
    public int Total { get; set; }
    /// <summary>已经开奖、能判定的场次。</summary>
    public int Settled { get; set; }
    /// <summary>若当时照打，命中的场次。</summary>
    public int Hits { get; set; }
    /// <summary>
    /// 若当时照打，命中率 %。
    /// 没有已开奖样本时为 null —— 不是 0。0 会被读成「一场都没中」，实际是「还没得判」。
    ///
    /// 这里显式要求把 null 写出来：全局配置了 WhenWritingNull，字段会被整个省掉，
    /// 前端拿到 undefined 分不清「没有这个字段」和「字段是空」。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public decimal? HitRate { get; set; }
    /// <summary>平均水位。</summary>
    public decimal? AvgOdds { get; set; }
    /// <summary>若当时每场平注 1 元，回报率（含本金，1.0 = 不亏不赚）。</summary>
    public decimal? Roi { get; set; }
}

/// <summary>否决台账的整体结论。</summary>
public sealed class VetoReport
{
    public int Days { get; set; }
    public int Total { get; set; }
    public int Settled { get; set; }
    public List<VetoStatRow> Rows { get; set; } = new();
    /// <summary>一句话结论：这些被挡的场次照打是赚是亏，样本够不够。</summary>
    public string Verdict { get; set; } = "";
}
