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
    /// <summary>
    /// 期望回报。**榜单对外一律报实证口径**（= <see cref="BaselineEv"/>，2026-11-08 改）：
    /// 模型概率 × 赔率是模型自证——模型越乐观，这个数越漂亮，而每个玩法的返奖率
    /// （半全场/总进球 0.797、比分 0.710）早把上限写死了。用户的教训就是在这里：
    /// 按模型 EV 挑注，挑到的恰好是模型高估最狠的那一注（江原负负模型 1.240 / 实证 0.762）。
    /// API 层会把本字段覆盖成 BaselineEv，模型原值移到 <see cref="ModelEv"/> 仅供对比。
    /// </summary>
    public decimal Ev { get; set; }

    /// <summary>模型自证口径的 EV（模型概率 × 赔率）。只在需要展示「模型有多乐观」时使用。</summary>
    public decimal? ModelEv { get; set; }

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
    /// 「模型高估」标记（2026-11-08 加，落库到 FbMilkPick.ModelOverconfident）：
    /// 该注名义 <see cref="Ev"/> 超过本玩法返奖率 × 1.05。
    ///
    /// 判据见 <see cref="Sources.MilkPickEngine.MarkOverconfident"/>。只标不撤。
    /// 前端看到这个标记要把 EV 显示换成「模型高估·天花板 X」口径——
    /// 半全场/总进球 0.837、比分 0.745、胜平负 0.922，超过就是模型把概率算高了，
    /// 不是发现了价值。与 <see cref="ModelLowConfidence"/> 方向相反但本质同源：
    /// 前者说「模型给低了」，后者说「模型给高了」，两者都是「模型这个数字别当真」。
    /// </summary>
    public bool ModelOverconfident { get; set; }

    /// <summary>
    /// 半全场「模型纯值 ÷ 市场去水隐含」（2026-11-08 加，落库到 FbMilkPick.ModelVsMarketRatio）。
    ///
    /// 落库的 <see cref="ModelProbability"/> 是 Blend 之后的混合值（0.7 模型 + 0.3 市场），
    /// 掺进去的那 0.3 正是市场自己的报价——所以它**不能**用来判断「模型信不信这个价」。
    /// 这个字段存的是**掺市场之前**的纯模型概率与市场隐含之比，1.0 = 模型与市场完全一致。
    ///
    /// 起因（2026-11-08 用户当场质问）：周日015 热那亚 vs AC米兰 负胜@36 被选成单列，
    /// 纯模型 1.21%、市场隐含 2.22%、混合后 1.51%。三道旧闸全部放行，因为没有一道
    /// 真的看过「模型自己对这注怎么看」。这个比值就是那道缺失的视角：0.55。
    ///
    /// 用法：&lt; 0.70（<see cref="Sources.MilkPickEngine.CounterHighModelVsMarketMin"/>）
    /// 即「模型认为市场高估 30% 以上」→ 单列直接否决、主榜打红标。
    /// **只标不撤适用于主榜**，单列是硬闸——单列是我们主动挑出来推荐的那一注，标准必须更严。
    /// </summary>
    public decimal? ModelVsMarketRatio { get; set; }

    /// <summary>
    /// 「模型不认这个价」的判定线：模型纯值 ÷ 市场隐含 &lt; 0.70，即模型认为市场高估 30% 以上。
    ///
    /// 放在 Core 而不是 <c>MilkPickEngine</c>，因为标记属性 <see cref="ModelDoubt"/> 要在模型层算，
    /// 而 Core 不能反向引用 Sources。引擎侧的 <c>CounterHighModelVsMarketMin</c> 直接引用这个常量，
    /// 保证「筛选」和「标记」用的是同一个数——这两处一旦漂移，就会出现「栏里被拒、榜上却挂绿标」。
    ///
    /// 取 0.70 的理由与「没有统计支撑」的坦白，见 <c>MilkPickEngine.CounterHighModelVsMarketMin</c> 注释。
    /// 一句话：它是风控约束，不是回测优化出来的参数。
    /// </summary>
    public const decimal ModelDoubtRatio = 0.70m;

    /// <summary>
    /// 该注「模型不认这个价」——比值为 null 时按未标记处理。
    /// 前端渲染成红色角标，文案必须写「模型认为市场高估 XX%」，不能只写「风险」。
    /// </summary>
    public bool ModelDoubt =>
        ModelVsMarketRatio is decimal r && r < ModelDoubtRatio;

    /// <summary>
    /// 半全场 胜平(3-1) / 负平(0-1) 的概率来自 <c>HalfDrawModel</c> 而不是引擎的 HalfFullJoint
    /// （2026-11-08 接入选注，落库到 FbMilkPick.HalfDrawRefined）。
    ///
    /// 为什么这两档要单独换概率源：竞彩对胜平和负平**报同一个价**（库内 552/552 差值为 0），
    /// 价格里不含方向信息，而 HalfFullJoint 只是泊松两段，对「半场领先被追平」和
    /// 「半场落后追平」的方向差分辨不足。HalfDrawModel 用竞彩总进球报价反推平局分布、
    /// 再乘 10340 场实测的方向率，495 场校准后 P(胜平) 5.06%（市场 5.07%、实际 4.90%）。
    ///
    /// 这两档**不掺盘口**（其余档照旧 Blend）：市场对两档给的价格一模一样，
    /// 掺进去就是把模型刚算出来的方向差重新抹平，等于白算。
    /// </summary>
    public bool HalfDrawRefined { get; set; }

    /// <summary>
    /// 热门方「体能/状态风险」标注（2026-11-08 加，落库到 FbMilkPick.FatigueTag）。
    /// 空 = 无风险标记。
    ///
    /// ⚠ 只标注、**不扣概率**。原因写在 <c>MilkPickEngine.FatigueRisk</c> 的注释里：
    /// 回测（2025-08 起，热门赔率≤2.50）显示「上一场欧战 + 96 小时内再战」的热门方
    /// 实际兑现 63.9% vs 市场隐含 59.1%（+4.8 点），三连败组同样偏正。
    /// 也就是说「累了的强队会输」在数据上不成立，扣分会是负 alpha。
    /// 保留标注是为了让每注都能被人工复核，以及等样本够了再回来复算。
    /// </summary>
    public string? FatigueTag { get; set; }

    /// <summary>
    /// 竞彩主胜「早盘→临场」升水幅度（%，2026-11-07 加，技能11）。null = 没有可用的开盘对照。
    /// ≥12% 时主胜方向注（胜胜/平胜/主胜比分）被 <see cref="MilkPickEngine"/> 整类撤下：
    /// 9758 场平均盘回测，升水≥12% 的主胜 ROI 33 vs 平盘基准 54——资金放弃主队是最可信的利空；
    /// 降水方向只确认既有判断、不产生超额价值（利好已被定价），故只否决升水、不奖励降水。
    /// </summary>
    public decimal? WaterRisePct { get; set; }

    /// <summary>
    /// 「主胜赔率偏贵档」标记（2026-11-11 加，落库到 FbMilkPick.HomePriceRich）。
    /// true = 该场收盘欧赔主胜落在 [2.90, 3.60) 且这一注押全场主胜。
    ///
    /// 依据（三个独立子集方向一致，但都不到 2σ，所以**只标不撤**）：
    /// 全球 2,903 场 n=289 实际 23.88% vs 隐含 28.51%（−4.64 点，1.6σ）；
    /// 竞彩受注联赛子集 361 场 n=33（−7.87 点，1.0σ）；竞彩实盘子集 105 场 n=8（0 命中）。
    /// 常量与完整边界见 <see cref="Sources.MilkPickEngine.HomePriceRichMin"/>。
    ///
    /// 前端文案只能说「历史同档兑现率低于收盘价隐含」，不能说「别买」——
    /// 我们的证据还撑不起后者。
    /// </summary>
    public bool HomePriceRich { get; set; }

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

    /// <summary>
    /// 竞彩/必发定价背离标签（2026-11-08 加，落库到 FbMilkPick.DivergenceTag）。
    /// fake-hot-away = 客胜被竞彩单边砸价、必发不认（该形态回测 -17 点，客胜注已撤）；
    /// fake-hot-home = 主胜同形态（回测 +3.4 点，仅标记不撤）；
    /// undervalued-home / undervalued-away = 该侧被竞彩单边抬价、必发不跟（回测被低估，仅标记）。
    /// 判定见 <see cref="PriceDivergence"/>。
    /// </summary>
    public string? DivergenceTag { get; set; }

    /// <summary>定价背离的中文说明（前端直接显示），没有背离形态时为 null。</summary>
    public string? DivergenceNote { get; set; }
}

/// <summary>
/// 单列候选的引擎判定结果（2026-11-08 加，只做展示，不参与选注）。
///
/// 起因：候选区接口原先在 API 层重新跑三道闸，唯一漏掉的是「模型不得强烈否定」——
/// 那道闸需要模型的三向概率，API 层拿不到。结果页面上「被模型否决」和「被赔率挤出」
/// 长得一模一样（11-08 热那亚 负胜@36 就是被模型否决的，但页面只显示「未上榜」）。
///
/// 现在由引擎在出榜时把每条候选的落点算出来落库（FbMilkCounterCandidate），
/// 接口直接读，原因可追溯。判定顺序与选注链严格一致，见 MilkPickEngine 里的诊断块。
/// </summary>
public static class MilkCounterVerdicts
{
    /// <summary>采纳入单列。</summary>
    public const string Admitted = "admitted";
    /// <summary>过了全部闸，但同档有更优的注被选中（每天只留 1 注）。</summary>
    public const string LostToRank = "lost-to-rank";
    /// <summary>模型概率 &lt; 联赛基线×0.40，引擎认为模型连方向都不认同。</summary>
    public const string ModelVeto = "model-veto";
    /// <summary>模型纯值 &lt; 市场隐含×0.70：模型自己都不信这个价，买它等于替高估买单。</summary>
    public const string ModelVsMarket = "model-vs-market";
    /// <summary>基线 EV / 返奖率 不在 [0.95, 1.35]。</summary>
    public const string RatioOutOfBand = "ratio-out-of-band";
    /// <summary>名义 EV 超全局上限，模型概率明显爆表。</summary>
    public const string EvTooHigh = "ev-too-high";
    /// <summary>该联赛该档实测概率 &lt; 1.20%（如友谊赛 0.05%）。</summary>
    public const string LeagueBelowFloor = "league-below-floor";
    /// <summary>联赛不在基线表里，拿不到可信频率。</summary>
    public const string NoLeagueBaseline = "no-league-baseline";
    /// <summary>该场已经占了主榜名额，同一场不重复出注。</summary>
    public const string SameMatchOnBoard = "same-match-on-board";

    /// <summary>给前端直接用的中文说明。</summary>
    public static string Label(string verdict) => verdict switch
    {
        Admitted => "已入单列",
        LostToRank => "同档被更优注挤出",
        ModelVeto => "模型否决",
        ModelVsMarket => "模型不认这个价",
        RatioOutOfBand => "赔率/基线不匹配",
        EvTooHigh => "模型概率爆表",
        LeagueBelowFloor => "联赛基线过低",
        NoLeagueBaseline => "联赛无基线",
        SameMatchOnBoard => "该场已在主榜",
        _ => verdict
    };
}

/// <summary>单列候选一行（FbMilkCounterCandidate）：引擎出榜时的判据快照。</summary>
public sealed class MilkCounterCandidate
{
    public long MatchId { get; init; }
    public string MatchNo { get; init; } = "";
    public string? LeagueName { get; init; }
    public string? HomeTeam { get; init; }
    public string? AwayTeam { get; init; }
    public DateTime? MatchTime { get; init; }
    public string OptionCode { get; init; } = "";
    public decimal Odds { get; init; }
    public decimal ModelProbability { get; init; }
    public decimal? BasePct { get; init; }
    public decimal? Ratio { get; init; }
    public string Verdict { get; init; } = MilkCounterVerdicts.LostToRank;
    public string? Reason { get; init; }
}
