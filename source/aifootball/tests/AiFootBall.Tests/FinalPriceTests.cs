using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 终版价口径（2026-11-10 加）。锁死三件事：
///   ① 终版价回报的分母是**全部已结算注数**，不是「有终版价的注数」——
///      北单只有命中的注有开奖 SP，拿它当分母会把回报算成 130%；
///   ② 覆盖率按**命中注**算，分母是命中数；
///   ③ 覆盖率不足 95% 时这个回报不许被当成账本。
/// </summary>
public class FinalPriceTests
{
    [Fact]
    public void Roi_分母是全部已结算注数_不是有价的注数()
    {
        // 10 注已结算，4 注命中，命中注开奖 SP 合计 9.0 → 回报 0.9
        Assert.Equal(0.9m, FinalPriceMath.Roi(10, 9.0m));
    }

    [Fact]
    public void Roi_没有已结算注时返回0而不是除零()
    {
        Assert.Equal(0m, FinalPriceMath.Roi(0, 0m));
        Assert.Equal(0m, FinalPriceMath.Roi(-3, 5m));
    }

    [Fact]
    public void 覆盖率_分母是命中注数()
    {
        // 4 注命中、其中 3 注拿得到开奖 SP → 75.0%
        Assert.Equal(75.0m, FinalPriceMath.HitCoverage(4, 3));
    }

    [Fact]
    public void 覆盖率_没有命中注时是0而不是100()
    {
        Assert.Equal(0m, FinalPriceMath.HitCoverage(0, 0));
    }

    [Fact]
    public void 覆盖率达标才算账本()
    {
        Assert.True(FinalPriceMath.Trustworthy(20, 20));   // 100%
        Assert.True(FinalPriceMath.Trustworthy(20, 19));   // 95%
        Assert.False(FinalPriceMath.Trustworthy(20, 18));  // 90%
        Assert.False(FinalPriceMath.Trustworthy(0, 0));    // 没样本
    }

    [Fact]
    public void 空跑切片_终版价口径只累加有终版价的命中注()
    {
        var rows = new List<PaperTradeRow>
        {
            new() { TradeId = 1, Odds = 2.00m, FinalOdds = 2.40m, Hit = true,  FrozenAt = DateTime.Today },
            new() { TradeId = 2, Odds = 3.00m, FinalOdds = null, Hit = true,  FrozenAt = DateTime.Today },
            new() { TradeId = 3, Odds = 5.00m, FinalOdds = 4.00m, Hit = false, FrozenAt = DateTime.Today },
            new() { TradeId = 4, Odds = 5.00m, FinalOdds = null, Hit = null,  FrozenAt = DateTime.Today },
            new() { TradeId = 5, Odds = 5.00m, FinalOdds = null, Hit = true,  Voided = true, FrozenAt = DateTime.Today }
        };

        var s = PaperTradeMath.Slice("全部", rows);

        Assert.Equal(3, s.Settled);            // 第 4 注未结算、第 5 注撤注，都不进分母
        Assert.Equal(2, s.Hits);
        Assert.Equal(5.00m, s.Return);         // 推荐价口径：2.00 + 3.00
        Assert.Equal(1.6667m, s.Roi);           // 5.00 ÷ 3，切片保留 4 位
        Assert.Equal(1, s.FinalHits);          // 命中的两注里只有一注有终版价
        Assert.Equal(2.40m, s.ReturnFinal);
        Assert.Equal(0.8m, s.RoiFinal);        // 2.40 ÷ 3（分母仍是全部已结算注数）
        Assert.Equal(50.0m, s.FinalCoverage);  // 1 ÷ 2 命中注
        Assert.False(s.FinalVerified);         // 覆盖率 50% < 95%，不许当账本
    }

    [Fact]
    public void 北单口径_未命中不算缺价()
    {
        // 北单只有命中的注有开奖 SP。10 注结算、6 命中、6 注都有 SP → 覆盖率 100%，回报可直接引用。
        var hits = 6;
        var finalHits = 6;
        Assert.Equal(100m, FinalPriceMath.HitCoverage(hits, finalHits));
        Assert.True(FinalPriceMath.Trustworthy(hits, finalHits));
        Assert.Equal(0.95m, FinalPriceMath.Roi(10, 9.5m));
    }
}
