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
    /// <summary>让球盘 SP 赔率（开奖前会变，参考值）。</summary>
    public decimal? HandicapOdds { get; set; }
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
