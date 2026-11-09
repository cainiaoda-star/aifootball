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
    /// 反解：blended = 0.7·model + 0.3·market → model = (blended − 0.3·market)/0.7。
    /// 用热那亚的实测数字验：blended 1.51%、odds 36 → 纯模型应约 1.21%。
    /// </summary>
    [Fact]
    public void ModelPctBeforeBlend_RecoversPureModelValue()
    {
        var pure = MilkPickEngine.ModelPctBeforeBlend(1.51m, 36m, BqOver);

        Assert.InRange(pure, 1.15m, 1.30m);
    }

    /// <summary>快照不全（overRound ≤ 0）时 Blend 不掺市场，反解必须原样返回，不能瞎推。</summary>
    [Fact]
    public void ModelPctBeforeBlend_PassesThrough_WhenNoMarket()
    {
        Assert.Equal(3.30m, MilkPickEngine.ModelPctBeforeBlend(3.30m, 36m, 0m));
        Assert.Equal(3.30m, MilkPickEngine.ModelPctBeforeBlend(3.30m, 1m, BqOver));
    }

    /// <summary>
    /// 热那亚 负胜@36 的真实数字：纯模型 1.21%、市场隐含 2.22%，比值 0.55。
    /// 这条断言的意义是——如果哪天比值算出来变成 0.8 以上，说明有人又把市场掺回去了。
    /// </summary>
    [Fact]
    public void GenoaCase_RatioIsAboutHalf()
    {
        var pure = MilkPickEngine.ModelPctBeforeBlend(1.51m, 36m, BqOver);
        var market = 100m / 36m / BqOver;

        Assert.InRange(market, 2.15m, 2.30m);
        Assert.InRange(pure / market, 0.50m, 0.60m);
        Assert.True(pure / market < MilkPickEngine.CounterHighModelVsMarketMin,
            "模型认为市场高估近一半，必须低于 0.70 门槛才拦得住——这条一旦失败，热那亚那注会重新上榜。");
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
