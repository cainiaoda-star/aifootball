namespace AiFootBall.Core.Models;

/// <summary>
/// 竞彩大奶推荐一行：某场某玩法（比分/半全场/总进球）的某个选项。
/// 2026-11-04 改向：胜负彩、北单推荐下线，每日只发竞彩大奶。
/// </summary>
public sealed class MilkPick
{
    public string Board { get; set; } = Boards.Jc;
    public string MatchNo { get; set; } = "";
    public long? MatchId { get; set; }
    public string? LeagueName { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }

    /// <summary>BF=比分 / BQ=半全场 / JQ=总进球（JcPlayTypes 常量）。</summary>
    public string PlayType { get; set; } = "";
    /// <summary>选项代码：比分 "3:1"、半全场 "3-0"（半-全，3胜1平0负）、总进球 "0".."7"（7=7+）。</summary>
    public string OptionCode { get; set; } = "";
    /// <summary>显示名：比分 3:1 / 半全场 胜负 / 总进球 4球。</summary>
    public string OptionName { get; set; } = "";

    /// <summary>出票时的固定奖金快照。</summary>
    public decimal Odds { get; set; }
    /// <summary>模型概率 0-100（Dixon-Coles 比分网格）。</summary>
    public decimal ModelProbability { get; set; }
    /// <summary>期望回报 = 概率 × 赔率。长期看小于 1 就是亏，列出只为排序。</summary>
    public decimal Ev { get; set; }

    /// <summary>
    /// 这一注的方向与市场最强方向相反（2026-11-05 加）。
    ///
    /// 起因是周四011 阿尔克马 vs 尤文图斯：市场客胜 1.71（去水 51.4%），模型却发「胜胜」，
    /// EV 1.106 看着漂亮，实际是拿模型对跨联赛实力差的误判去跟整个市场对赌。
    /// 方向闸当时判了 diverge，但 EnforceDirectionVeto 关着，只提示不砍，注照样上榜。
    ///
    /// 现在改成：单注方向与市场方向相反时打这个标记给前端画红标；市场方向占比过半时直接撤注
    /// （见 MilkPickEngine.DivergentDropPct）。总进球没有胜负方向，恒为 false。
    /// </summary>
    public bool DivergedFromMarket { get; set; }

    /// <summary>市场最强方向（3 主胜 / 1 平 / 0 客胜），用于提示文案。</summary>
    public char? MarketDirection { get; set; }

    /// <summary>市场最强方向的去水占比（%），如 51.4。</summary>
    public decimal? MarketDirectionPct { get; set; }

    /// <summary>
    /// 这一注的概率有没有掺过盘口（2026-11-05 加）。
    ///
    /// false = 该玩法当时的快照档数不全（比分不足 31 档 / 半全场不足 9 档 / 总进球不足 8 档），
    /// 引擎退回纯模型概率。这类注的 EV 会系统性偏高，因为它少了市场那一半的约束，
    /// 前端必须标「纯模型」——不然看着像挖到价值，其实只是没有参照物。
    /// </summary>
    public bool MarketPriced { get; set; } = true;

    public string? MarketDirectionName => MarketDirection switch
    {
        '3' => "主胜",
        '1' => "平局",
        '0' => "客胜",
        _ => null
    };

    public string PlayName => PlayType switch
    {
        JcPlayTypes.Bf => "比分",
        JcPlayTypes.Bq => "半全场",
        JcPlayTypes.Jq => "总进球",
        _ => PlayType
    };

    /// <summary>
    /// 零封倾向方向（2026-11-06 加，仅引擎内部传递、不落库）：'3'=主队零封对手（客队全场0球）、
    /// '0'=客队零封主队（主队全场0球）、null=无显著零封倾向。
    /// 用于半全场选注的统一决策：零封概率高时优先选「负负/胜胜」方向一致档，而非硬套高赔反转档。
    /// </summary>
    public char? CleanSheetDirection { get; set; }

    /// <summary>
    /// 「让胜陷阱」标记（2026-11-06 加，仅引擎内部传递、不落库）。
    /// true = 这场主队让胜(NSPF-3)被资金买到极端低水（&lt;= <see cref="MilkPickEngine.MinNspfHotOdds"/>），
    /// 且模型净胜球卡在 1 球（主队大概率「只赢球、不赢盘」）。此时该押「让平 + 胜胜 + 1球小胜」，
    /// 而不是追让胜（让胜需净胜≥2，会走盘）。与「必发主胜大热反向」同源。
    /// </summary>
    public bool HotHomeTrap { get; set; }
}
