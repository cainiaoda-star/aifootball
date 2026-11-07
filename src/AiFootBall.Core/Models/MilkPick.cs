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

    /// <summary>
    /// 「低水杯赛热门」衰减标记（2026-11-07 加，仅引擎内部传递、不落库）。
    /// true = 杯赛/欧战 + 竞彩主胜 ≤<see cref="MilkPickEngine.CupHotFadeMaxOdds"/> +
    /// 必发主胜大热(≥70%)或钱涌客胜(PnlIdxAway≤-60)。
    /// 依据：欧罗巴主胜&lt;1.35 场次翻车率 81.8%（回测 11 场）、主胜&lt;1.35 低水热门 6 场反转率 67%，
    /// 半全场「平负(1-0)/负负(0-0)」是最高频落点（AC米兰0:2/勒沃库森0:1/桑德兰0:2 全是这个剧本）。
    /// 命中时该档在 <see cref="MilkPickEngine"/> 的半全场档位里提到与零封同级。
    /// </summary>
    public bool CupHotFade { get; set; }

    /// <summary>
    /// 「高赔反转单列」标记（2026-11-07 加，落库到 FbMilkPick.CounterHigh）。
    /// true = 这一注来自半全场反转档的独立名额，不在主榜的 3 个半全场名额里竞争。
    ///
    /// 为什么单列：主榜排序按模型概率，反转档概率天然 2%~5%，永远挤不过胜胜/负负/平平，
    /// 于是 30 倍以上的档位在主榜里「结构上不存在」。而用户要的恰恰是这个量级。
    /// 单列名额每期最多 1 注，前端必须标注「高赔单列·长期负期望」，
    /// 不许混进主榜装作能赚——反转档的 EV 天花板仍是玩法返奖率（0.797）。
    /// </summary>
    public bool CounterHigh { get; set; }

    /// <summary>
    /// 联赛基线的实测概率（%，2026-11-07 夜加，仅单列注有意义）。
    /// 取 <see cref="Sources.LeagueHalfFullBaseline"/> 里该联赛该反转档的历史发生频率。
    ///
    /// 为什么要单独存一份而不是用 ModelProbability：模型在极端反转档上系统性低估。
    /// 实证 2026-11-07 周六007 朴茨茅斯 vs 斯托克城（英冠，半场主胜→全场 1:2，胜负@32）：
    /// 模型给 1.76%，英冠实测 2.56%，市场去水隐含 2.50%——模型比两边都低 30%。
    /// 那一注被模型自己的概率门槛挡死，一注没发。单列票的可信度来源是「这个联赛到底会不会翻」，
    /// 不是「模型敢不敢给」。前端展示单列注时用 <see cref="BaselineEv"/>，不用 Ev。
    /// </summary>
    public decimal? BaselineProbability { get; set; }

    /// <summary>
    /// 按联赛基线算的 EV = BaselineProbability/100 × Odds（2026-11-07 夜加）。
    /// 单列注唯一该给用户看的期望值——模型 EV 在这种长尾档上不可信（同 <see cref="BaselineProbability"/>）。
    /// 天花板仍是玩法返奖率 0.797 的 ×1.15 容差内，不能用来声称「有价值」。
    /// </summary>
    public decimal? BaselineEv { get; set; }

    /// <summary>
    /// 模型在该注上的概率「低可信」标记（2026-11-07 夜加，落库到 FbMilkPick.ModelLowConfidence）。
    ///
    /// 判据：模型概率 &lt; 联赛基线 × <see cref="Sources.MilkPickEngine.ModelLowConfidenceRatio"/>(0.70)。
    /// 只对半全场反转档计算——顺风档（胜胜/负负/平平）样本充足、模型可信，不标。
    ///
    /// 为什么需要它：2026-11-07 周六007 朴茨茅斯 vs 斯托克城（英冠，胜负@32），模型给 1.76%，
    /// 英冠 3,248 场实测 2.56%，市场去水隐含 2.50%。模型比两边都低 30%，却因为
    /// 「模型概率 &lt; 门槛」被直接判成垃圾注、一注没发。这不是模型算错了一个数字，
    /// 是泊松 λ 在长尾档上系统性低估（λ 的 MAE 1.53 球，市场反推 1.33）。
    /// 标记的作用：让用户看到「模型说不值、但这个联赛历史上就是会发生」，
    /// 而不是让模型单方面的低概率把一注有历史依据的票静默抹掉。
    /// </summary>
    public bool ModelLowConfidence { get; set; }

    /// <summary>
    /// 竞彩主胜「早盘→临场」升水幅度（%，2026-11-07 加，技能11）。null = 没有可用的开盘对照。
    /// ≥12% 时主胜方向注（胜胜/平胜/主胜比分）被 <see cref="MilkPickEngine"/> 整类撤下：
    /// 9758 场平均盘回测，升水≥12% 的主胜 ROI 33 vs 平盘基准 54——资金放弃主队是最可信的利空；
    /// 降水方向只确认既有判断、不产生超额价值（利好已被定价），故只否决升水、不奖励降水。
    /// </summary>
    public decimal? WaterRisePct { get; set; }

    /// <summary>
    /// 本场模型热门方（'3'=主队 / '0'=客队，2026-11-07 俱乐部画像落地）。null = 没有画像或没分出方向。
    /// </summary>
    public char? FavoriteSide { get; set; }

    /// <summary>热门方的历史反转率（%，全库口径见 <see cref="TeamProfile"/>）。null = 无画像。</summary>
    public decimal? FavoriteReversalPct { get; set; }

    /// <summary>
    /// 热门方是「高反转队」（反转率≥<see cref="MilkPickEngine.ReversalFavoriteMinRate"/>，广岛/尤文/蔚山/马竞档）。
    /// 这类队领先被翻是系统性现象，半全场「热门反向」的低赔反转档（平负/负负 或 平胜/负胜）提级。
    /// </summary>
    public bool HighReversalFavorite { get; set; }

    /// <summary>
    /// 热门方是「碾压型真强队」（反转率≤<see cref="MilkPickEngine.CrushReversalMaxRate"/> 且零封率≤
    /// <see cref="MilkPickEngine.CrushCleanSheetMaxRate"/>，拜仁/巴黎/勒沃库森档）。
    /// 这类队该赢就真赢，杯赛低水热门的反向提级（CupHotFade）对它们失效——防止拿假热门规律硬套真碾压。
    /// </summary>
    public bool CrushFavorite { get; set; }

    /// <summary>
    /// 平局倾向（2026-11-07）：模型全场平局概率 ≥ <see cref="MilkPickEngine.DrawSetupThreshold"/>
    /// （30%，全库基准约 26%）。此时半全场「平平」是该场的第一落点，提级处理。
    /// 之前引擎只有胜/负两个方向的哨兵（零封/杯热/画像全围绕热门），平局完全没人看——
    /// 而平平在联赛占 22.7%（仅次于胜胜 38.6%），是第三大半全场档。
    /// </summary>
    public bool DrawSetup { get; set; }

    /// <summary>
    /// 让平命中（2026-11-07，技能10落地）：深盘让-1 + 主胜大热(≤1.5) 或 让胜陷阱 →
    /// 「赢球不赢盘」，让球盘走盘=让平。10.8 万场回测：大热+让-1 让平 26.4%、EV 0.92，
    /// 显著高于让胜的 0.78。此前 NSPF 从来不出注，这是全引擎最大的价值缺口。
    /// </summary>
    public bool HandicapDrawSetup { get; set; }
}
