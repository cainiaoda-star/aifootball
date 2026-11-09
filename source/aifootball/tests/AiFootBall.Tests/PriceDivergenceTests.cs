using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 竞彩/必发单边定价背离（2026-11-08 加，曼城复盘落地）。
///
/// 这些断言守的是**判据形状**，不是回测数值：
/// ① 只有「竞彩动得大 + 必发不跟」才算背离，两边一起动不算；
/// ② 降赔侧 = 虚热，升赔侧 = 被低估，两者不能混；
/// ③ 缺任一侧价格必须返回 null —— 半个数据下的结论比不下结论更危险；
/// ④ 10% / 3% 是闭区间边界（恰好 10.0% 要算命中，9.9% 不算）。
///
/// ⚠ 不要在这里断言「客胜虚热必定翻车」。样本只有 13 场（p≈0.21），
/// 这条闸的价值是「方向明确 + 机制清楚 + 可回退」，不是「已被证明」。
/// </summary>
public class PriceDivergenceTests
{
    /// <summary>造一份 PriceSpan：竞彩主/客首末 + 必发主/客首末，全部按「降价为正」的直觉给原始赔率。</summary>
    private static PriceDivergence.PriceSpan Span(
        decimal jcHomeOpen, decimal jcHomeCurr,
        decimal jcAwayOpen, decimal jcAwayCurr,
        decimal exHomeOpen, decimal exHomeCurr,
        decimal exAwayOpen, decimal exAwayCurr)
        => new(jcHomeOpen, jcHomeCurr, jcAwayOpen, jcAwayCurr,
               exHomeOpen, exHomeCurr, exAwayOpen, exAwayCurr);

    [Fact]
    public void 客胜被砸价而必发不动判虚热()
    {
        // 曼城场原样：竞彩客胜 1.60→1.43（-10.6%），必发 3.10→3.14（-1.3%，即没动）
        var r = PriceDivergence.Judge(Span(4.15m, 4.30m, 1.60m, 1.43m, 4.20m, 4.25m, 3.10m, 3.14m));

        Assert.NotNull(r);
        Assert.True(r!.AwayFakeHot);
        Assert.False(r.HomeFakeHot);
        Assert.Equal("fake-hot-away", r.Tag);
        Assert.Contains("客胜", r.Note);
    }

    [Fact]
    public void 两边一起降不算背离()
    {
        // 竞彩客胜 -10.6%、必发也降 9% —— 专业资金在跟，这是真信息，不能撤
        var r = PriceDivergence.Judge(Span(4.15m, 4.30m, 1.60m, 1.43m, 4.20m, 4.25m, 3.10m, 2.82m));

        Assert.NotNull(r);
        Assert.False(r!.AwayFakeHot);
        Assert.Null(r.Tag);
    }

    [Fact]
    public void 主胜虚热与客胜虚热分开标()
    {
        var r = PriceDivergence.Judge(Span(2.20m, 1.90m, 3.40m, 3.50m, 2.30m, 2.32m, 3.30m, 3.28m));

        Assert.NotNull(r);
        Assert.True(r!.HomeFakeHot);
        Assert.False(r.AwayFakeHot);
        Assert.Equal("fake-hot-home", r.Tag);
    }

    [Fact]
    public void 竞彩抬价而必发不跟判被低估()
    {
        // 竞彩主胜 2.20→2.60（升 18%），必发几乎不动 → 该侧被低估（回测 +30.8 点，18 场）
        var r = PriceDivergence.Judge(Span(2.20m, 2.60m, 3.40m, 3.30m, 2.30m, 2.32m, 3.30m, 3.28m));

        Assert.NotNull(r);
        Assert.Equal('3', r!.UndervaluedSide);
        Assert.Equal("undervalued-home", r.Tag);
        Assert.False(r.HomeFakeHot);
    }

    [Fact]
    public void 缺必发价格则不下结论()
    {
        // 必发列全 null（当天下架/未采到）→ 两侧都判不了
        var r = PriceDivergence.Judge(new PriceDivergence.PriceSpan(
            4.15m, 4.30m, 1.60m, 1.43m, null, null, null, null));

        Assert.Null(r);
    }

    [Fact]
    public void 只缺客胜必发时主胜侧照常判()
    {
        var r = PriceDivergence.Judge(new PriceDivergence.PriceSpan(
            2.20m, 1.90m, 3.40m, 3.50m, 2.30m, 2.32m, null, null));

        Assert.NotNull(r);
        Assert.True(r!.HomeFakeHot);
        Assert.Null(r.Away);        // 客胜侧没有必发价，不许拿半个数据凑
    }

    [Fact]
    public void 阈值是闭区间边界()
    {
        // 恰好降 10.0% 命中；降 9.9% 不命中
        var hit = PriceDivergence.Judge(Span(4.00m, 4.00m, 2.00m, 1.80m, 4.00m, 4.00m, 3.00m, 3.00m));
        var miss = PriceDivergence.Judge(Span(4.00m, 4.00m, 2.00m, 1.802m, 4.00m, 4.00m, 3.00m, 3.00m));

        Assert.True(hit!.AwayFakeHot);
        Assert.False(miss!.AwayFakeHot);

        // 必发动 2.9% 仍算「没跟」，3.1% 就不算了
        var flat = PriceDivergence.Judge(Span(4.00m, 4.00m, 2.00m, 1.80m, 4.00m, 4.00m, 3.00m, 2.913m));
        var moved = PriceDivergence.Judge(Span(4.00m, 4.00m, 2.00m, 1.80m, 4.00m, 4.00m, 3.00m, 2.907m));
        Assert.True(flat!.AwayFakeHot);
        Assert.False(moved!.AwayFakeHot);
    }

    /// <summary>
    /// 撤注口径：只有「注指客胜 + 客胜虚热」才撤。
    /// 主胜虚热不撤（回测 +3.4 点）、无方向不撤（总进球押的是球数）、非虚热不撤。
    /// </summary>
    [Fact]
    public void 撤注只对客胜方向生效()
    {
        var awayHot = PriceDivergence.Judge(Span(4.15m, 4.30m, 1.60m, 1.43m, 4.20m, 4.25m, 3.10m, 3.14m));
        var homeHot = PriceDivergence.Judge(Span(2.20m, 1.90m, 3.40m, 3.50m, 2.30m, 2.32m, 3.30m, 3.28m));

        Assert.True(PriceDivergence.ShouldVeto('0', awayHot));       // 押客胜 + 客胜虚热 → 撤
        Assert.False(PriceDivergence.ShouldVeto('3', awayHot));      // 押主胜，客胜虚热跟它无关
        Assert.False(PriceDivergence.ShouldVeto(null, awayHot));     // 总进球没有方向，不撤
        Assert.False(PriceDivergence.ShouldVeto('3', homeHot));      // 主胜虚热不撤（回测无偏离）
        Assert.False(PriceDivergence.ShouldVeto('0', null));         // 没有背离数据，不下判断
    }

    /// <summary>
    /// 曼城场原样回放（2026-11-08 01:30 英超 诺丁汉森林 3:1 曼城，库内首末价）：
    /// 竞彩客胜 1.60→1.44（-10.0%），必发 1.78→1.76（-1.1%）。
    /// 这一条是这条闸的「为什么存在」——如果哪天判定不出这个形态了，说明阈值或取价口径被改了。
    /// </summary>
    [Fact]
    public void 曼城场回放应判客胜虚热并撤注()
    {
        var r = PriceDivergence.Judge(Span(4.15m, 4.85m, 1.60m, 1.44m, 4.90m, 5.00m, 1.78m, 1.76m));

        Assert.NotNull(r);
        Assert.True(r!.AwayFakeHot);
        Assert.True(PriceDivergence.ShouldVeto('0', r));
        // 那晚押的正是「比分 0:1」和「半全场 平负」——两注都指客胜，都该在这道闸前消失
        Assert.True(PriceDivergence.ShouldVeto('0', r));
    }
}
