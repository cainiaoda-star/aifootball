using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 市场权重分玩法 + 两道闸分离（2026-11-11 加）。
///
/// 这组测试守的不是数值，而是**结构**，因为结构错了会静默：
/// 1) 权重必须按玩法取，不许再出现裸的单一常数；
/// 2) 安全闸读纯模型，价值闸读混合概率——写反了闸门会随 w 自动变松/变紧，代码里看不出来；
/// 3) 反解只用于读历史行，运行时不许依赖它。
/// </summary>
public class WeightAndGateTests
{
    const decimal BqOver = 1.254m;

    [Fact]
    public void MarketWeightFor_MapsEveryPlayType()
    {
        // 数值目前全是占位（0.3），但映射必须存在：删掉这个 switch 就等于
        // 所有玩法又回到共用一个常数，而那正是这次要拆掉的东西。
        Assert.Equal(MilkPickEngine.MarketWeightSpf, MilkPickEngine.MarketWeightFor(JcPlayTypes.Spf));
        Assert.Equal(MilkPickEngine.MarketWeightNspf, MilkPickEngine.MarketWeightFor(JcPlayTypes.Nspf));
        Assert.Equal(MilkPickEngine.MarketWeightBq, MilkPickEngine.MarketWeightFor(JcPlayTypes.Bq));
        Assert.Equal(MilkPickEngine.MarketWeightJq, MilkPickEngine.MarketWeightFor(JcPlayTypes.Jq));
        Assert.Equal(MilkPickEngine.MarketWeightBf, MilkPickEngine.MarketWeightFor(JcPlayTypes.Bf));
        // 未知玩法不能悄悄按最激进的值走。
        Assert.Equal(MilkPickEngine.MarketWeightDefault, MilkPickEngine.MarketWeightFor(null));
        Assert.Equal(MilkPickEngine.MarketWeightDefault, MilkPickEngine.MarketWeightFor("XYZ"));
    }

    /// <summary>
    /// 安全闸必须读纯模型。这里给一个「纯模型很低、混合值很高」的组合：
    /// 若哪天有人把安全闸改回读 ModelProbability，这条会立刻失败。
    /// </summary>
    [Theory]
    [InlineData(5.0, 40.0)]   // 纯模型 5% < 12% 门槛，混合 40% 再漂亮也不该过
    [InlineData(11.99, 60.0)]
    public void SafetyGate_ReadsPureModel_NotBlended(decimal purePct, decimal blendedPct)
    {
        var (safety, _) = MilkPickEngine.JudgeGates(purePct, blendedPct, 7.0m, JcPlayTypes.Bq, "3-3");
        Assert.False(safety);
    }

    /// <summary>
    /// 价值闸必须读混合概率。纯模型再高，混合后 EV 低于门槛就不该买——
    /// 因为混合后才是我们真正相信的那个数。
    /// </summary>
    [Fact]
    public void ValueGate_ReadsBlended_NotPure()
    {
        var payback = MilkPickEngine.Payout(JcPlayTypes.Bq);            // 0.797
        var line = payback * MilkPickEngine.MinEvPaybackRatio;

        // 纯模型 40% 很高，但混合只有 3% → EV = 0.03 * 8 = 0.24，远低于线。
        var (safety, value) = MilkPickEngine.JudgeGates(40m, 3m, 8m, JcPlayTypes.Bq, "3-3");
        Assert.True(safety);
        Assert.False(value);

        // 同一注只要混合概率抬到线上，就该过。
        var needPct = line / 8m * 100m;                                  // 约 10.5%
        var (_, value2) = MilkPickEngine.JudgeGates(40m, needPct + 0.1m, 8m, JcPlayTypes.Bq, "3-3");
        Assert.True(value2);
    }

    /// <summary>
    /// 反转档的门槛随赔率走（返奖率/赔率×0.85，夹在 [1.5, 12]）。
    /// 32 倍档的公平概率只有 2.5%，拿 12% 去要求它等于要求模型比市场准一倍。
    /// </summary>
    [Fact]
    public void ReverseFloor_ScalesWithOdds()
    {
        // 16 倍反转档：0.797/16*100*0.85 ≈ 4.23%
        Assert.False(MilkPickEngine.JudgeGates(4.1m, 20m, 16m, JcPlayTypes.Bq, "1-0").Safety);
        Assert.True(MilkPickEngine.JudgeGates(4.4m, 20m, 16m, JcPlayTypes.Bq, "1-0").Safety);

        // 顺风档（半场=全场）仍走固定 12%。
        Assert.False(MilkPickEngine.JudgeGates(11.9m, 20m, 16m, JcPlayTypes.Bq, "3-3").Safety);
        Assert.True(MilkPickEngine.JudgeGates(12.1m, 20m, 16m, JcPlayTypes.Bq, "3-3").Safety);
    }

    [Fact]
    public void NonReverseFloors_PerPlayType()
    {
        Assert.False(MilkPickEngine.JudgeGates(9.9m, 20m, 4m, JcPlayTypes.Jq, "2").Safety);
        Assert.True(MilkPickEngine.JudgeGates(10.1m, 20m, 4m, JcPlayTypes.Jq, "2").Safety);
    }

    /// <summary>
    /// 反解与混合互为逆运算（读历史行时用）。用 w=0.3 的路由。
    /// </summary>
    [Fact]
    public void ModelPctBeforeBlend_RoundTripsAtCurrentWeight()
    {
        const decimal odds = 10m;
        const decimal pure = 20m;
        var marketPct = 100m / odds / BqOver;                            // ≈7.97
        var blended = (1m - MilkPickEngine.MarketWeightFor(JcPlayTypes.Bq)) * pure
                      + MilkPickEngine.MarketWeightFor(JcPlayTypes.Bq) * marketPct;

        var back = MilkPickEngine.ModelPctBeforeBlend(blended, odds, BqOver, JcPlayTypes.Bq);
        Assert.Equal(pure, back, 2);
    }

    /// <summary>
    /// 快照不全（overRound ≤ 0）时 Blend 不掺市场，纯模型值应等于混合值——
    /// 反解此时必须原样返回，不许乱推。
    /// </summary>
    [Fact]
    public void ModelPctBeforeBlend_PassesThroughWhenNoMarket()
    {
        Assert.Equal(17.5m, MilkPickEngine.ModelPctBeforeBlend(17.5m, 10m, 0m, JcPlayTypes.Bq));
    }
}
