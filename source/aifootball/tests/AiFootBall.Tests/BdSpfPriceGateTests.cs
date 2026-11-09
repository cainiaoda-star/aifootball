using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 北单出榜的「无价注」闸（2026-11-09 加）。
///
/// 事故：2026-11-09 16:41 那期，克里斯蒂安松 vs 瓦勒伦加（平手盘）被推成「让负」，
/// 让球概率 41.3% 排在榜上，但 `Odds` / `HandicapOdds` 全是 NULL ——
/// 该场北单页面没抓到让球 SP。后果有三：
///   ① 用户看到一注买不到价的票；
///   ② 台账里空价行，结算时进分母但不进分子；
///   ③ 串关算不出回报。
///
/// 为什么北单要硬拦、而竞彩 `ForJcHandicap` 允许「水位未知保留」：
/// 竞彩是单关，回报可以事后补；北单是串关，缺一腿整串作废，缺价注没有任何意义。
/// </summary>
public class BdSpfPriceGateTests
{
    private static BdSpfPick Pick(decimal? hcpOdds, char side = '3', string no = "145")
        => new()
        {
            MatchId = 1,
            MatchNo = no,
            LeagueName = "挪超",
            HomeTeam = "克里斯蒂安松",
            AwayTeam = "瓦勒伦加",
            Handicap = 0m,
            HandicapSide = side,
            HandicapSideName = side == '3' ? "让胜" : "让负",
            HandicapProbability = 41.3m,
            HandicapOdds = hcpOdds,
            Odds = hcpOdds,
            Kind = "steady"
        };

    [Theory]
    [InlineData(1.69)]
    [InlineData(2.25)]
    [InlineData(2.61)]
    [InlineData(2.71)]
    [InlineData(15.0)]
    public void 有价注放行(double odds)
        => Assert.True(BdSpfPickService.HasUsablePrice(Pick((decimal)odds)));

    [Fact]
    public void 无价注被拦_当日实例()
        => Assert.False(BdSpfPickService.HasUsablePrice(Pick(null, '0')));

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(-2.0)]
    public void 非法价等同无价(double odds)
        => Assert.False(BdSpfPickService.HasUsablePrice(Pick((decimal)odds)));

    [Fact]
    public void 当日四注全部有价_一注被拦()
    {
        // 2026-11-09 那期的真实数据：5 注里 145 无价，其余四注有价。
        var picks = new[]
        {
            Pick(null, '0', "145"),      // 145 克里斯蒂安松 让负 —— 无价
            Pick(1.69m, '3', "338"),     // 338 拉斯帕尔马斯 让胜
            Pick(2.61m, '3', "366"),     // 366 博托沙尼 让胜
            Pick(2.71m, '3', "367"),     // 367 哈维德夫 让胜
            Pick(2.25m, '3', "369")      // 369 托里什恩斯 让胜
        };

        var kept = picks.Where(BdSpfPickService.HasUsablePrice).ToList();

        Assert.Equal(4, kept.Count);
        Assert.DoesNotContain(kept, p => p.MatchNo == "145");
        Assert.All(kept, p => Assert.True(p.HandicapOdds > 1m));
    }
}
