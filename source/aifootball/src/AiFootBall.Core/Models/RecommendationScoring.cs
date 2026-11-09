namespace AiFootBall.Core.Models;

/// <summary>推荐是否算一次有效预测，以及和赛果比较后是否命中。</summary>
public static class RecommendationScoring
{
    /// <summary>竞彩/北单对外发布、以及默认命中率计入的最低置信度。</summary>
    public const decimal PublishConfidence = 60m;

    /// <summary>
    /// 精选门槛：校准后的预计命中率。写死的 61.3、73.8、85.7 不能代替它。
    /// 北单 2020–2024 训练、2025–2026 验证：模型让球最高项全样本约 44%，但 ≥55% 这一档验证命中 57.3%（944 场），50–55% 只有 50.9%。门槛停在 55，不降。
    /// </summary>
    public const decimal MinFeaturedHitProbability = 55m;

    /// <summary>
    /// 推荐赔率下限（2026-11-04 用户明确指示：低赔一律不推，宁可高赔、宁可空缺）。
    /// 起因：11-03 的 2串1 两腿 1.47/1.53，组合赔率 2.25、中奖率 34.3%、EV 0.771，
    /// 0/2 全输。≥55% 命中率的让球盘赔率天然落在 1.45–1.60，概率门槛越高票越温吞。
    /// 让球盘 -1 档位概率和赔率一一对应，想要高赔就必须接受概率让位——所以
    /// 票腿的 55% 概率门槛降为 <see cref="MinTicketLegProbability"/>，赔率下顶上来当第一道闸。
    /// 注意：这**不是** EV 优化（1.80×45% 单腿 EV 0.81，未必好过 1.47×60% 的 0.88），
    /// 是赔率结构偏好：输了认，不玩温水。北单暂不套用。
    /// </summary>
    public const decimal MinPickOdds = 1.80m;

    /// <summary>
    /// 票腿的最低让球概率（垃圾过滤）。低于它的方向模型自己都不信，再高赔也不组票。
    /// 取代原先的 55% 门槛——55% 与赔率 ≥1.80 在让球盘上几乎互斥，两个都留着等于永远无票。
    /// </summary>
    public const decimal MinTicketLegProbability = 45m;

    /// <summary>低于此水位只标注。1.50 及以下注明奖金薄，1.50 到 1.90 注明不是稳胆。不抬置信度。</summary>
    public const decimal MinPublishOdds = 1.90m;

    /// <summary>
    /// 经验命中率要把一条推荐踢出精选，至少需要这么多已结算场次。
    /// 12 场的 95% 区间约 ±28 个百分点，50% 和 55% 在这个量级下统计上没有区别，
    /// 拿它否决 944 场验证出来的 55 门槛是反的。40 场区间约 ±15 点，才够做降级依据。
    /// </summary>
    public const int MinSampleForDemote = 40;

    /// <summary>只调置信度、不动精选等级所需的最小样本。到这个量级点估计已经有参考价值，但不该一票否决。</summary>
    public const int MinSampleForConfidence = 20;

    /// <summary>
    /// 必发单侧占比达到这个数，算「极端大热」，这一侧不追。
    /// 60–75 只是偏热，仍可留在推荐里。
    ///
    /// 这个数原先在三处各写一遍：HotWatch.Line、RecommendationEngine.ExtremeHot、
    /// HitProbabilityService 里一个裸 75m。三处是同一个规则的三道关卡（挪到展示区、
    /// 引擎不追热、模型改向时不跟风），改一处漏两处就会出现「引擎放行了、展示区却挪走」
    /// 这种自相矛盾的场次。收敛到这里，只有一个数值来源。
    ///
    /// 动它之前先看否决台账：heat-extreme 那一行的「照打回报」过 1.0 才说明这条线挡错了。
    /// 2026-11-03 让球表 8 场被削到 6 场就是这条线的副作用，但那不构成放宽的证据 ——
    /// 少两场不等于那两场会中。
    /// </summary>
    public const decimal ExtremeHotRatio = 75m;

    public static bool IsDirectionalPick(char? recResult, bool isCoverAll)
        => !isCoverAll && recResult is '3' or '1' or '0';

    /// <summary>全包或没有方向时返回 null，不参与命中率。</summary>
    public static bool? DirectionalHit(char? recResult, bool isCoverAll, char actualResult)
    {
        if (!IsDirectionalPick(recResult, isCoverAll)) return null;
        return recResult == actualResult;
    }

    public static bool CountsInHitRate(char? recResult, bool isCoverAll, decimal? confidence)
        => IsDirectionalPick(recResult, isCoverAll) && confidence >= PublishConfidence;
}
