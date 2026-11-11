using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 「模型纯值 vs 市场隐含」这条闸的守门测试（2026-11-08 加）。
///
/// 起因：周日015 热那亚 vs AC米兰 负胜@36 被选成单列，三道旧闸全部放行——
/// 因为它们比的都是 Blend 之后的混合值，而 Blend 掺的 30% 正是市场自己的报价。
/// 这里锁死三件事，防止有人把口径改回去：
///   ① 反解公式正确（blended 减掉市场那部分能还原纯模型值）；
///   ② 比值低于 0.70 时 ModelDoubt 必须为真；
///   ③ 热那亚那组真实数字算出来的比值必须是 0.55 而不是「通过」。
/// </summary>
public class ModelVsMarketTests
{
    /// <summary>半全场倒数和，与 MilkPickEngine.StandardOverRound(Bq) 一致。</summary>
    private const decimal BqOver = 1.254m;

    /// <summary>
    /// 反解：blended = (1-w)·model + w·market → model = (blended − w·market)/(1-w)。
    /// 用 w 无关的写法验公式：给定纯模型 1.21、36 倍的市场隐含 2.215，先正向混合再反解，
    /// 必须回到 1.21。这样权重以后不论怎么改，这条都只在公式写错时才红。
    /// </summary>
    [Fact]
    public void ModelPctBeforeBlend_RecoversPureModelValue()
    {
        const decimal pure = 1.21m;
        const decimal market = 100m / 36m / BqOver;                       // ≈2.215
        var w = MilkPickEngine.MarketWeightFor(JcPlayTypes.Bq);
        var blended = (1m - w) * pure + w * market;

        var back = MilkPickEngine.ModelPctBeforeBlend(blended, 36m, BqOver, JcPlayTypes.Bq);

        Assert.InRange(back, pure - 0.01m, pure + 0.01m);
    }

    /// <summary>快照不全（overRound ≤ 0）时 Blend 不掺市场，反解必须原样返回，不能瞎推。</summary>
    [Fact]
    public void ModelPctBeforeBlend_PassesThrough_WhenNoMarket()
    {
        Assert.Equal(3.30m, MilkPickEngine.ModelPctBeforeBlend(3.30m, 36m, 0m));
        Assert.Equal(3.30m, MilkPickEngine.ModelPctBeforeBlend(3.30m, 1m, BqOver));
    }

    /// <summary>
    /// 热那亚 负胜@36 的真实数字：市场隐含 2.22%，纯模型 1.21%，比值 0.55 → 被拦。
    ///
    /// ★ 那行历史记录的 blended 是 **1.51（w=0.3 时代的落库值）**，拿现在的 w=0.5 去反解
    /// 同一个数只会得到 0.80——这不是公式坏了，而正是「运行时不许反解历史行」的原因
    /// （见 ModelPctBeforeBlend 的注释第 2 条）。所以这里改成直接验比值本身：
    /// 结论必须是「模型认为市场高估近一半 ⇒ 拦」，换任何 w 都不该变。
    /// </summary>
    [Fact]
    public void GenoaCase_RatioIsAboutHalf()
    {
        const decimal market = 100m / 36m / BqOver;
        const decimal pure = 1.21m;
        var ratio = pure / market;

        Assert.InRange(market, 2.15m, 2.30m);
        Assert.InRange(ratio, 0.50m, 0.60m);
        Assert.True(ratio < MilkPickEngine.CounterHighModelVsMarketMin,
            "模型认为市场高估近一半，必须低于 0.70 门槛才拦得住——这条一旦失败，热那亚那注会重新上榜。");

        // 反向锁：w 真被改大以后，用当天的 blended 反解会得到偏低的「伪纯模型值」。
        // 这个数低于真实值 1.21，正是「反解值随 w 漂移」的证据，别再拿它做跨天比较。
        var drifted = MilkPickEngine.ModelPctBeforeBlend(1.51m, 36m, BqOver, JcPlayTypes.Bq);
        Assert.True(drifted < pure, $"w=0.5 时反解 1.51 得到 {drifted:F3}，应低于真实纯模型值——否则说明公式与权重脱钩");
    }

    /// <summary>比值低于门槛 → ModelDoubt 为真；高于门槛或为空 → 不标红。</summary>
    [Theory]
    [InlineData(0.55, true)]
    [InlineData(0.69, true)]
    [InlineData(0.70, false)]
    [InlineData(0.81, false)]
    [InlineData(1.79, false)]
    public void ModelDoubt_FlagsOnlyBelowThreshold(decimal ratio, bool expected)
    {
        var pick = new MilkPick { ModelVsMarketRatio = ratio };
        Assert.Equal(expected, pick.ModelDoubt);
    }

    /// <summary>没算过比值（快照不全）时不能误标红。</summary>
    [Fact]
    public void ModelDoubt_IsFalse_WhenRatioMissing()
    {
        Assert.False(new MilkPick().ModelDoubt);
    }

    /// <summary>
    /// 筛选与标记必须用同一个阈值。
    /// 这两处一旦漂移，就会出现「引擎拒了这注、页面却挂绿标」或者反过来——
    /// 2026-11-08 之前那道 0.40 闸就是活例子（用混合值判、拿基线比，谁也没错、谁也没查出来）。
    /// </summary>
    [Fact]
    public void Threshold_IsSharedBetweenFilterAndMark()
    {
        Assert.Equal(MilkPick.ModelDoubtRatio, MilkPickEngine.CounterHighModelVsMarketMin);
    }
}
