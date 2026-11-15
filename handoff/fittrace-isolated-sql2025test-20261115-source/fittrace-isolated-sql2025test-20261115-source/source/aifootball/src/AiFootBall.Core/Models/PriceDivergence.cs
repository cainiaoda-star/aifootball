namespace AiFootBall.Core.Models;

/// <summary>
/// 竞彩 / 必发「单边定价」背离判定（2026-11-08 加）。
///
/// 起因：2026-11-08 01:30 英超 诺丁汉森林 3:1 曼彻斯特城。竞彩客胜 1.60 → 1.43（-10.6%），
/// 而必发 PriceAway 只从开盘挪了 -1.1%。两边在讲两件不同的事：
///   · 竞彩是散户盘子，热门被反复砸价；
///   · 必发是专业资金，价格几乎没动，说明没人愿意按这个价接。
/// 结果曼城输了，我们押的「0:1 比分」和「半全场平负」两注全挂。
///
/// 这不是「热门必翻」（见下），而是一个更窄的形态：**一侧被单边砸价、另一侧不认**。
///
/// 回测（2026-11-08，口径：FbJcOddsHistory 的 SPF 首末价 + FbExchange 的 PriceHome/PriceAway 首末，
/// 完赛场次，去水隐含取收盘三档）：
///
///   形态                         n    该侧实际  收盘隐含   偏离
///   客胜 降≥10% + 必发动&lt;3%     13    23.1%     40.1%    -17.0
///   主胜 降≥10% + 必发动&lt;3%      9    55.6%     52.2%     +3.4
///   客胜 升≥10% + 必发动&lt;3%     40    40.0%     27.1%    +12.9
///   主胜 升≥10% + 必发动&lt;3%     18    61.1%     30.3%    +30.8
///   其余基线                   428    31.5%     33.0%     -1.5
///
/// 三个必须写下来的结论：
///  1. **降赔侧被高估只在客胜方向成立**（13 场 -17.0 点），主胜方向反而 +3.4 点（9 场）。
///     所以撤注闸只对客胜侧开，主胜侧只打标——这是数据的不对称，不是漏写。
///     13 场样本仍小（二项检验 p≈0.21），效应量大、机制清楚，但**不足以当铁律**，
///     因此撤注开关 <see cref="VetoAwayFakeHot"/> 单独留常量，随时可关。
///  2. **「必发一侧大热」本身不是反向信号**。同一批数据里必发客胜占比 ≥80% 的 59 场，
///     客胜实际 69.5% vs 收盘隐含 62.8%（高兑现 6.7 点）。所谓「大热必翻」在静态资金上是错的，
///     真正的信息在「两个市场是否讲同一件事」。
///  3. **升赔侧被低估的方向更一致、样本更厚**（两侧合计 58 场，实际 46.6% vs 隐含约 28.5%）。
///     这一条与现有 MilkPickEngine.WaterRiseVetoPct（主胜升水≥12% 撤主胜注）方向相反，
///     但样本量级差太多（18 场 vs 9758 场），**不许拿它去改那条规则**，
///     这里只打「被市场低估」标记供观察。
///
/// 判定是纯函数，数据由调用方注入，便于单测与事后回放。
/// </summary>
public static class PriceDivergence
{
    /// <summary>竞彩一侧的移动幅度门槛（%）：涨跌都按这个数算「单边大动」。</summary>
    public const decimal JcMovePct = 10m;

    /// <summary>必发同侧「没跟上」的判定线（%）：移动幅度小于它就算不认这个价。</summary>
    public const decimal ExFlatPct = 3m;

    /// <summary>
    /// 客胜侧「虚热」是否撤注（默认开）。
    ///
    /// 只对客胜侧生效，依据见类注释第 1 条：客胜降≥10% 且必发不跟的 13 场，实际 23.1%，
    /// 比收盘隐含低 17 个点；主胜同一形态 9 场是 +3.4 点，撤了就是白扔注。
    /// 关掉它只影响撤注，打标与漏斗计数照常。
    /// </summary>
    public static readonly bool VetoAwayFakeHot = true;

    /// <summary>
    /// 这一注的方向是否落在「虚热」侧、应当撤下（2026-11-08）。
    ///
    /// 抽成纯函数是为了可测：引擎里那段是 1800 行的大循环，撤注逻辑埋在中间，
    /// 单测只能靠「跑一遍 BuildDetailed 看结果对不对」，而那要喂进整套模型快照。
    /// 把判定抽出来，规则本身就能被钉死。
    ///
    /// 只认客胜侧（'0'）——主胜同形态回测 +3.4 点，撤了等于白扔注。
    /// 没有方向（总进球）不撤：它押的是球数，不是这一侧的胜负。
    /// </summary>
    public static bool ShouldVeto(char? pickDirection, Result? divergence)
        => VetoAwayFakeHot && pickDirection == '0' && divergence is { AwayFakeHot: true };

    /// <summary>
    /// 一场比赛的定价输入。所有 (Open, Curr) 都是「该市场最早的报价 / 此刻最新报价」。
    /// 任一侧取不到就留 null，判定会自动跳过那一侧，绝不猜。
    /// </summary>
    public readonly record struct PriceSpan(
        decimal? JcOpenHome, decimal? JcCurrHome,
        decimal? JcOpenAway, decimal? JcCurrAway,
        decimal? ExOpenHome, decimal? ExCurrHome,
        decimal? ExOpenAway, decimal? ExCurrAway);

    /// <summary>一侧的移动明细。</summary>
    public readonly record struct SideMove(decimal JcPct, decimal ExPct)
    {
        /// <summary>竞彩降赔（跌 ≥ <see cref="JcMovePct"/>）。</summary>
        public bool JcDropped => JcPct >= JcMovePct;

        /// <summary>竞彩升赔（涨 ≥ <see cref="JcMovePct"/>）。</summary>
        public bool JcRose => JcPct <= -JcMovePct;

        /// <summary>必发没跟上（动幅 &lt; <see cref="ExFlatPct"/>）。</summary>
        public bool ExFlat => Math.Abs(ExPct) < ExFlatPct;

        /// <summary>竞彩砸价、必发不认 → 这一侧被散户单边定价。</summary>
        public bool IsFakeHot => JcDropped && ExFlat;

        /// <summary>竞彩抬价、必发不认 → 这一侧被市场低估（回测方向为正）。</summary>
        public bool IsUndervalued => JcRose && ExFlat;
    }

    /// <summary>判定结果。两侧都可能为 null，表示数据不足或没有该形态。</summary>
    public sealed class Result
    {
        /// <summary>主胜侧移动。null = 竞彩或必发缺主胜首末价。</summary>
        public SideMove? Home { get; init; }

        /// <summary>客胜侧移动。null = 竞彩或必发缺客胜首末价。</summary>
        public SideMove? Away { get; init; }

        /// <summary>
        /// 客胜侧「虚热」：竞彩客胜降 ≥10% 而必发几乎不动。
        /// 这是唯一会触发撤注的形态（<see cref="VetoAwayFakeHot"/>）。
        /// </summary>
        public bool AwayFakeHot => Away is { } a && a.IsFakeHot;

        /// <summary>主胜侧「虚热」。只打标，不撤注——回测该形态无偏离（+3.4 点，9 场）。</summary>
        public bool HomeFakeHot => Home is { } h && h.IsFakeHot;

        /// <summary>某一侧被市场低估（竞彩抬价、必发不认）。只打标。</summary>
        public char? UndervaluedSide
            => Home is { } h && h.IsUndervalued ? '3'
             : Away is { } a && a.IsUndervalued ? '0'
             : null;

        /// <summary>给前端/落库的一句话，没有形态时返回 null。</summary>
        public string? Note
        {
            get
            {
                if (AwayFakeHot)
                    return $"客胜被竞彩单边砸价（{Away!.Value.JcPct:0.#}%），必发只动 {Away.Value.ExPct:0.#}% —— 专业资金不认这个价，客胜方向注已撤";
                if (HomeFakeHot)
                    return $"主胜被竞彩单边砸价（{Home!.Value.JcPct:0.#}%），必发只动 {Home.Value.ExPct:0.#}%（该形态回测无偏离，仅标记）";
                if (UndervaluedSide is char u)
                {
                    var mv = u == '3' ? Home!.Value : Away!.Value;
                    var name = u == '3' ? "主胜" : "客胜";
                    return $"{name}被竞彩单边抬价（{-mv.JcPct:0.#}%），必发不跟 —— 该侧回测被低估，仅标记";
                }
                return null;
            }
        }

        /// <summary>落库用的短标签：fake-hot-away / fake-hot-home / undervalued-home / undervalued-away。</summary>
        public string? Tag
            => AwayFakeHot ? "fake-hot-away"
             : HomeFakeHot ? "fake-hot-home"
             : UndervaluedSide switch { '3' => "undervalued-home", '0' => "undervalued-away", _ => null };
    }

    /// <summary>
    /// 判一场。取不到价就返回 null（宁可不出结论，也不拿半个数据下判断）。
    /// </summary>
    public static Result? Judge(PriceSpan s)
    {
        var home = Move(s.JcOpenHome, s.JcCurrHome, s.ExOpenHome, s.ExCurrHome);
        var away = Move(s.JcOpenAway, s.JcCurrAway, s.ExOpenAway, s.ExCurrAway);
        if (home is null && away is null) return null;
        return new Result { Home = home, Away = away };
    }

    /// <summary>
    /// 单侧移动百分比。返回值**正数 = 降价**（越降越热），与读赔率的方向一致：
    /// (开 - 现) / 开。这样「JcPct ≥ 10」就是「降了 10% 以上」。
    /// </summary>
    private static SideMove? Move(decimal? jcOpen, decimal? jcCurr, decimal? exOpen, decimal? exCurr)
    {
        if (jcOpen is not > 1m || jcCurr is not > 1m) return null;
        if (exOpen is not > 1m || exCurr is not > 1m) return null;
        var jc = (jcOpen.Value - jcCurr.Value) / jcOpen.Value * 100m;
        var ex = (exOpen.Value - exCurr.Value) / exOpen.Value * 100m;
        return new SideMove(Math.Round(jc, 1), Math.Round(ex, 1));
    }
}
