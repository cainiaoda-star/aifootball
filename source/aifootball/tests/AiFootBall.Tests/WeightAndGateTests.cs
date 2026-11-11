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
        // 数值本身由下面 FrozenWeights 那条锁；这里只守映射：
        // 删掉这个 switch 就等于所有玩法又回到共用一个常数，而那正是这次要拆掉的东西。
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
    /// 2026-11-11 冻结的那组值。**它们不是从 216 注 ROI 里选出来的**，
    /// 而是按「市场越有效 ⇒ 市场权重越高」的先验定的（总进球另有一条：模型没有信息）。
    ///
    /// 这条测试的作用是把「别再拿噪声去调它」写成可执行的形式：谁想改数值，
    /// 必须先改这里，并回答「有效样本够了吗、这是不是又拿 216 注的排序当证据」。
    /// 只守大小关系是不够的——把 0.7/0.5/0.3 全调成 0.5 依然单调，但结构已经没了。
    /// </summary>
    [Fact]
    public void FrozenWeights_ArePriorNotRoiFitted()
    {
        Assert.Equal(0.7m, MilkPickEngine.MarketWeightSpf);    // 3 档、去水最稳
        Assert.Equal(0.7m, MilkPickEngine.MarketWeightNspf);   // 同胜平负
        Assert.Equal(0.5m, MilkPickEngine.MarketWeightBq);     // 9 档、中等
        Assert.Equal(0.3m, MilkPickEngine.MarketWeightBf);     // 31 档、长尾去水最不可靠
        Assert.Equal(0.8m, MilkPickEngine.MarketWeightJq);     // 特例：模型无信息，市场主导
        Assert.Equal(0.5m, MilkPickEngine.MarketWeightDefault);
        // 结构：市场越有效 ⇒ 越高。比分必须低于半全场，半全场必须低于胜平负。
        Assert.True(MilkPickEngine.MarketWeightBf < MilkPickEngine.MarketWeightBq);
        Assert.True(MilkPickEngine.MarketWeightBq < MilkPickEngine.MarketWeightSpf);
        // 总进球高于半全场是「模型没信息」导致的，不是「8 档比 9 档去水更准」。
        Assert.True(MilkPickEngine.MarketWeightJq > MilkPickEngine.MarketWeightBq);
    }

    /// <summary>
    /// 总进球不走模型闸：纯模型再低（这里是 0.5%，远低于 10% 门槛）也不许被它否决，
    /// 因为校准实测 JQ 纯模型比历史频率还差 1.04 个点（方向反）——那是噪声，不是判断。
    /// 否决权交给市场：4.00 的价去水后隐含 19.9%，该出；12.00 的价只剩 6.6%，不该出。
    /// </summary>
    [Fact]
    public void Jq_GatesByMarket_NotByModel()
    {
        Assert.False(MilkPickEngine.UsesModelGates(JcPlayTypes.Jq));
        Assert.True(MilkPickEngine.UsesModelGates(JcPlayTypes.Bq));
        Assert.True(MilkPickEngine.UsesModelGates(JcPlayTypes.Bf));

        var (safetyLoose, valueLoose) = MilkPickEngine.JudgeGates(0.5m, 20m, 4.0m, JcPlayTypes.Jq, "3");
        Assert.True(safetyLoose);   // 纯模型 0.5% 也放行——市场说这档 19.9%
        Assert.True(valueLoose);    // 价值档由实证闸（PassesBaselineEv）在流水线里判，这里不设

        var (safetyTight, _) = MilkPickEngine.JudgeGates(60m, 60m, 12.0m, JcPlayTypes.Jq, "3");
        Assert.False(safetyTight);  // 纯模型 60% 也救不了——市场只给 6.6%
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

    /// <summary>
    /// 各玩法固定门槛（顺风档）：半全场 12%、比分 5%。
    ///
    /// 总进球不再走这条：2026-11-11 起它由市场准入判（见 <see cref="Jq_GatesByMarket_NotByModel"/>），
    /// 所以这里不再断言「纯模型 9.9% 的 JQ 注会被拦」——按新口径，只要市场给够就会放行，
    /// 那条断言留着就等于把已经改掉的口径又焊回测试里。
    /// </summary>
    [Fact]
    public void NonReverseFloors_PerPlayType()
    {
        Assert.False(MilkPickEngine.JudgeGates(11.9m, 20m, 16m, JcPlayTypes.Bq, "3-3").Safety);
        Assert.True(MilkPickEngine.JudgeGates(12.1m, 20m, 16m, JcPlayTypes.Bq, "3-3").Safety);

        Assert.False(MilkPickEngine.JudgeGates(4.9m, 20m, 30m, JcPlayTypes.Bf, "1:0").Safety);
        Assert.True(MilkPickEngine.JudgeGates(5.1m, 20m, 30m, JcPlayTypes.Bf, "1:0").Safety);
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

    // ── 归一化（2026-11-11 加）：混合后 Σ 必须回到 1 ──────────────────────
    //
    // 市场隐含用固定水位去水，Σ(1/赔率)/水位 并不精确等于 1（实测完整快照
    // BQ/JQ 均值 0.9905、最差 0.9582）。不归一化时每档概率集体偏低，
    // EV 与价值闸跟着系统性偏低。这组测试锁的是「Σ 回到 100」，不是某个具体因子。

    /// <summary>构造一个玩法快照，Σ(1/赔率) 精确等于给定的 sumInv。</summary>
    static List<JcOddsHistoryPoint> Snapshot(string playType, string[] codes, decimal sumInv)
    {
        // 每档赔率相同 ⇒ 1/赔率 = sumInv / n，Σ = sumInv，可倒着控制偏离量。
        var odds = codes.Length / sumInv;
        return codes.Select(c => new JcOddsHistoryPoint
        {
            PlayType = playType,
            OptionCode = c,
            Odds = odds
        }).ToList();
    }

    [Fact]
    public void BlendNormalizer_IsOneWhenMarketSumsToOne()
    {
        // 9 档、Σ1/赔率 = 1.254（正好等于水位）⇒ 去水后 Σ = 1 ⇒ 不该有任何调整。
        var codes = new[] { "3-3", "3-1", "3-0", "1-3", "1-1", "1-0", "0-3", "0-1", "0-0" };
        var pts = Snapshot(JcPlayTypes.Bq, codes, 1.254m);

        var norm = MilkPickEngine.BlendNormalizer(pts, JcPlayTypes.Bq, 1.254m);
        Assert.Equal(1m, norm, 3);
    }

    [Fact]
    public void BlendNormalizer_LiftsWhenMarketSumsBelowOne()
    {
        // Σ1/赔率 = 1.20 而水位 1.254 ⇒ 去水后 Σ 只有 0.9567。
        // 不归一化时混合概率整体偏低 1.3%（w=0.3），因子必须把这部分补回来。
        var codes = new[] { "3-3", "3-1", "3-0", "1-3", "1-1", "1-0", "0-3", "0-1", "0-0" };
        var pts = Snapshot(JcPlayTypes.Bq, codes, 1.20m);

        var w = MilkPickEngine.MarketWeightFor(JcPlayTypes.Bq);
        var expected = 1m / ((1m - w) + w * (1.20m / 1.254m));
        // 上界 = 市场完全缺失（Σ市场 → 0）时的因子 1/(1-w)；下界 = 完全不缺时的 1。
        var cap = 1m / (1m - w);

        var norm = MilkPickEngine.BlendNormalizer(pts, JcPlayTypes.Bq, 1.254m);
        Assert.Equal(expected, norm, 4);
        Assert.True(norm > 1m);
        Assert.True(norm < cap);
    }

    [Fact]
    public void BlendNormalizer_NormalizedSumIsOne()
    {
        // 端到端：9 档等赔率快照，混合 + 归一化后 Σ 必须回到 100.00。
        var codes = new[] { "3-3", "3-1", "3-0", "1-3", "1-1", "1-0", "0-3", "0-1", "0-0" };
        var pts = Snapshot(JcPlayTypes.Bq, codes, 1.20m);
        var over = 1.254m;
        var norm = MilkPickEngine.BlendNormalizer(pts, JcPlayTypes.Bq, over);
        var w = MilkPickEngine.MarketWeightFor(JcPlayTypes.Bq);

        // 模型侧给一个和为 1 的分布（这里用均匀，归一化只关心和）。
        var model = Enumerable.Repeat(1m / codes.Length, codes.Length).ToArray();
        var sum = 0m;
        for (var i = 0; i < codes.Length; i++)
            sum += ((1m - w) * model[i] + w * (1m / pts[i].Odds / over)) * norm;

        Assert.Equal(1m, sum, 4);
    }

    [Fact]
    public void BlendNormalizer_NoSnapshotMeansNoAdjustment()
    {
        // 快照不全时 overRound = 0，引擎回退纯模型，此时因子必须是 1，
        // 否则会去「归一化」一个本来就没掺市场的值。
        var codes = new[] { "3-3", "3-1" };
        var pts = Snapshot(JcPlayTypes.Bq, codes, 0.5m);
        Assert.Equal(1m, MilkPickEngine.BlendNormalizer(pts, JcPlayTypes.Bq, 0m));
    }
}
