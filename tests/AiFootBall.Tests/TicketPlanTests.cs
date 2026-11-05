using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 票型对比的数学验证。核心结论：期望回报只由返还率决定，与腿数无关；
/// 腿数只改变「中奖频次」和「回本概率」，不改变长期亏损率。
/// 注：奖金按 2 元/注、四舍五入到分计算，所以期望回报会有千分位上的误差，
/// 断言用区间而不是精确值。
/// </summary>
public sealed class TicketPlanTests
{
    private const decimal R = 0.905m; // 竞彩胜平负近似返还率

    /// <summary>给定真实命中率 p，市场会开出 R/p 的水位。</summary>
    private static (decimal Odds, decimal P) Leg(decimal p) => (R / p, p);

    private static string Dump(string name, (decimal AnyPrize, decimal BackStake, decimal Expected) plan, decimal stake)
        => $"{name}: 有奖 {plan.AnyPrize:0.0000} / 回本 {plan.BackStake:0.0000} / 期望 {plan.Expected:0.0000} / 本金 {stake}";

    [Fact]
    public void 单关_中奖率等于回本率_期望回报约等于返还率()
    {
        var plan = RecommendationSelection.JcCoverTicket.Plan(new[] { Leg(0.60m) }, "单关");
        var msg = Dump("单关", plan, 2m);

        Assert.True(Math.Abs(plan.AnyPrize - 0.60m) < 0.001m, msg);
        // 单关水位必然 >1，中奖即回本
        Assert.True(Math.Abs(plan.BackStake - plan.AnyPrize) < 0.001m, msg);
        Assert.True(plan.Expected is > 0.89m and < 0.92m, msg);
    }

    [Fact]
    public void 二串一全包两腿_中奖率是乘积_期望回报约等于返还率平方()
    {
        var plan = RecommendationSelection.JcCoverTicket.Plan(new[] { Leg(0.60m), Leg(0.60m) }, "2串1");
        var msg = Dump("2串1×2", plan, 2m);

        Assert.True(Math.Abs(plan.AnyPrize - 0.36m) < 0.001m, msg);
        Assert.True(plan.Expected is > 0.80m and < 0.84m, msg);
    }

    [Fact]
    public void 二串一全包四腿_中奖率明显高于回本率()
    {
        var legs = new[] { Leg(0.60m), Leg(0.60m), Leg(0.60m), Leg(0.60m) };
        var plan = RecommendationSelection.JcCoverTicket.Plan(legs, "2串1");
        var msg = Dump("2串1×4", plan, 12m);

        // 4 腿 6 注 12 元：中 2 场就有奖，所以中奖率很高；
        // 但两注小赔率的奖金经常不够 12 元，回本率要低得多。
        Assert.True(plan.AnyPrize > 0.80m, msg);
        Assert.True(plan.BackStake < plan.AnyPrize - 0.20m, msg);
        Assert.True(plan.Expected is > 0.80m and < 0.84m, msg);
    }

    [Fact]
    public void 八腿长串_中奖率低且期望回报最低()
    {
        var legs = Enumerable.Repeat(Leg(0.60m), 8).ToArray();
        var plan = RecommendationSelection.JcCoverTicket.Plan(
            legs, RecommendationSelection.JcCoverTicket.PassFor(8));
        var msg = Dump("6+7+8串1×8", plan, 74m);

        // 6 串 1 起，60% 的腿需要至少中 6 场才有奖：C(8,6)+C(8,7)+C(8,8) 的加权和约 31.5%
        Assert.True(plan.AnyPrize is > 0.29m and < 0.34m, msg);
        // 期望回报被抽水乘了 6~8 次
        Assert.True(plan.Expected < 0.60m, msg);
    }

    [Fact]
    public void 腿数越多期望回报越低()
    {
        var legs = Enumerable.Repeat(Leg(0.60m), 8).ToArray();
        var single = RecommendationSelection.JcCoverTicket.Plan(new[] { legs[0] }, "单关");
        var double2 = RecommendationSelection.JcCoverTicket.Plan(legs.Take(2).ToArray(), "2串1");
        var double4 = RecommendationSelection.JcCoverTicket.Plan(legs.Take(4).ToArray(), "2串1");

        var msg = string.Join(" | ",
            Dump("单关", single, 2m), Dump("2串1×2", double2, 2m), Dump("2串1×4", double4, 12m));

        Assert.True(single.Expected > double2.Expected, msg);
        // 2串1 无论几腿，期望回报都是 R²（腿数不改变它，只改变中奖分布）
        Assert.True(Math.Abs(double2.Expected - double4.Expected) < 0.01m, msg);
    }

    [Fact]
    public void 注数与本金_单关按腿数_二串一按组合数()
    {
        Assert.Equal(1, RecommendationSelection.JcCoverTicket.BetCountOf("单关", 1));
        Assert.Equal(4, RecommendationSelection.JcCoverTicket.BetCountOf("单关", 4));
        Assert.Equal(1, RecommendationSelection.JcCoverTicket.BetCountOf("2串1", 2));
        Assert.Equal(6, RecommendationSelection.JcCoverTicket.BetCountOf("2串1", 4));
        Assert.Equal(0, RecommendationSelection.JcCoverTicket.BetCountOf("2串1", 1));
        Assert.Equal(2m, RecommendationSelection.JcCoverTicket.StakeOf("单关", 1));
        Assert.Equal(12m, RecommendationSelection.JcCoverTicket.StakeOf("2串1", 4));
    }

    /// <summary>
    /// 票面概率取模型与盘口的较低值。原因是这两个数都不含经验校准，
    /// 模型的让球概率还是从欧赔折算的；用乐观的一边算回本率会系统性地好看。
    /// </summary>
    [Fact]
    public void 票面概率取两者较低值_不取平均也不取较高()
    {
        // 模型 57.8% / 盘口 59.7%：取 57.8%
        var low = RecommendationSelection.JcCoverTicket.PickProbability(0.578m, 0.597m);
        Assert.Equal(0.578m, low.Value);
        Assert.Equal(0.578m, low.Model);
        Assert.Equal(0.597m, low.Market);

        // 模型 61.1% / 盘口 57.4%：反过来，取 57.4%
        var low2 = RecommendationSelection.JcCoverTicket.PickProbability(0.611m, 0.574m);
        Assert.Equal(0.574m, low2.Value);

        // 高低是混着来的，不存在「模型永远偏乐观」，所以不能直接给模型打折
        Assert.True(low.Value != low2.Value);
    }

    [Fact]
    public void 票面概率_只有一边可用时用那一边_都不可用返回空()
    {
        var onlyModel = RecommendationSelection.JcCoverTicket.PickProbability(0.62m, null);
        Assert.Equal(0.62m, onlyModel.Value);
        Assert.Null(onlyModel.Market);

        var onlyMarket = RecommendationSelection.JcCoverTicket.PickProbability(null, 0.48m);
        Assert.Equal(0.48m, onlyMarket.Value);

        // 0 和 1 都不是可用概率：0 会让整张票的中奖率直接归零，1 是没算出来的占位
        var none = RecommendationSelection.JcCoverTicket.PickProbability(0m, 1m);
        Assert.Null(none.Value);
    }

    [Fact]
    public void 模型盘口分歧_按百分点给出_缺一边不算分歧()
    {
        Assert.Equal(1.9m, RecommendationSelection.JcCoverTicket.Disagreement(0.578m, 0.597m));
        Assert.Equal(3.7m, RecommendationSelection.JcCoverTicket.Disagreement(0.611m, 0.574m));
        Assert.Null(RecommendationSelection.JcCoverTicket.Disagreement(0.6m, null));
        Assert.Null(RecommendationSelection.JcCoverTicket.Disagreement(null, 0.6m));
    }

    [Fact]
    public void 让球票_八腿是上限_超过八腿不生成票型也不生成注数()
    {
        // 常态是 2–3 腿（票腿要过 55%），八腿只是竞彩过关上限，不是默认结构
        Assert.Equal("单关#2串1#3串1", RecommendationSelection.JcCoverTicket.PassFor(3));
        Assert.Equal("4串1#5串1#6串1", RecommendationSelection.JcCoverTicket.PassFor(6));
        Assert.Equal("6串1#7串1#8串1", RecommendationSelection.JcCoverTicket.PassFor(8));
        Assert.Equal(37, RecommendationSelection.JcCoverTicket.BetCountFor(8));
        Assert.Equal(74m, RecommendationSelection.JcCoverTicket.StakeFor(8));

        // 竞彩最高 8 串 1：9 腿以上没有合法票型，宁可返回 0 也不生成 9串1 的假注数
        Assert.Equal("", RecommendationSelection.JcCoverTicket.PassFor(9));
        Assert.Equal(0, RecommendationSelection.JcCoverTicket.BetCountFor(9));
        Assert.Equal(0m, RecommendationSelection.JcCoverTicket.StakeFor(12));
        Assert.Equal(0, RecommendationSelection.JcCoverTicket.BetCountOf("单关", 9));

        var nine = Enumerable.Range(0, 9).Select(_ => (Odds: 2m, P: 0.6m)).ToArray();
        Assert.Equal((0m, 0m, 0m), RecommendationSelection.JcCoverTicket.Chances(nine));
        Assert.Equal(0m, RecommendationSelection.JcCoverTicket.AllHit(Enumerable.Repeat(2m, 9).ToArray()));
        Assert.Equal((0m, 0m), RecommendationSelection.JcCoverTicket.OneMiss(Enumerable.Repeat(2m, 9).ToArray()));
    }
}
