using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 北单让球胜平负结算口径（2026-11-09 新增 bd-spf-settle 时锁死）。
///
/// 背景：这张台账在此之前从来没结算过，46 行 Hit 全 NULL，「北单成功率」只能靠临时 SQL 回算。
/// 补结算时最容易错的三处：
///   ① 让球数用错盘（FbMatch.Handicap 是竞彩盘，不是北单盘）；
///   ② 走盘（净胜恰好等于让球数）算成赢或输——它是让平，押让胜/让负都输；
///   ③ 受让方向搞反（+1 是主队受让，不是主队让 1 球）。
/// 下面用 11-06~11-08 的真实比分做回归，场次名与台账一致。
/// </summary>
public class BdSpfSettlementTests
{
    // ---- 让球后结果 ----

    [Theory]
    // 主让 1 球（-1）
    [InlineData(3, 1, -1, '3')]   // 净胜 2，让后 +1 → 让胜
    [InlineData(2, 1, -1, '1')]   // 净胜 1，让后 0  → 走盘，让平
    [InlineData(1, 1, -1, '0')]   // 净胜 0，让后 -1 → 让负
    [InlineData(2, 5, -1, '0')]   // 净胜 -3
    // 主受让 1 球（+1）
    [InlineData(2, 1, 1, '3')]    // 净胜 1，让后 +2
    [InlineData(1, 2, 1, '1')]    // 输 1 球 → 让后 0，走盘
    [InlineData(0, 2, 1, '0')]    // 输 2 球 → 让后 -1
    // 平手
    [InlineData(1, 1, 0, '1')]
    [InlineData(0, 3, 0, '0')]
    // 受让 2 球（弱队深盘）
    [InlineData(1, 2, 2, '3')]    // 输 1 球但受让 2 → 让胜
    [InlineData(0, 3, 2, '0')]    // 输 3 球 → 让后 -1
    // 让 2 球
    [InlineData(4, 1, -2, '3')]   // 净胜 3 → 让后 +1
    [InlineData(2, 1, -2, '0')]   // 净胜 1 → 让后 -1
    [InlineData(2, 0, -2, '1')]   // 净胜 2 → 走盘
    public void Result_AfterHandicap(int home, int away, int hcp, char expected)
        => Assert.Equal(expected, BdSpfSettlement.Result(home, away, hcp));

    // ---- 真实场次回归（2026-11-06~11-08 台账） ----

    [Fact]
    public void Algerish_2v1_Plus1_IsHandicapWin()
    {
        // 罗甲 阿尔杰什 2:1 布加勒斯特迪纳摩，北单让 +1（主受让）→ 让后 +2，让胜。
        // 台账押让胜 @2.91（实购 4.25）→ WIN。
        Assert.Equal('3', BdSpfSettlement.Result(2, 1, 1m));
        Assert.True(BdSpfSettlement.Judge('3', 2, 1, 1m));
    }

    [Fact]
    public void Krakow_2v1_Minus1_IsPush_AndLosesBothSides()
    {
        // 波兰甲 克拉科夫维斯瓦 2:1 克拉科维亚，让 -1 → 走盘。
        // 台账押让负 → LOSE；这正是「走盘把两侧都杀掉」的典型，绝不能判成某一侧赢。
        Assert.Equal('1', BdSpfSettlement.Result(2, 1, -1m));
        Assert.False(BdSpfSettlement.Judge('3', 2, 1, -1m));
        Assert.False(BdSpfSettlement.Judge('0', 2, 1, -1m));
        Assert.True(BdSpfSettlement.Judge('1', 2, 1, -1m));
    }

    [Fact]
    public void Aarau_0v3_Minus1_IsHandicapLoss()
    {
        // 瑞士甲 阿劳 0:3 韦尔，让 -1 → 让后 -4，让负。台账押让负 @2.07 → WIN。
        Assert.Equal('0', BdSpfSettlement.Result(0, 3, -1m));
        Assert.True(BdSpfSettlement.Judge('0', 0, 3, -1m));
    }

    [Fact]
    public void Rosenborg_4v1_Minus2_IsHandicapWin_NotLoss()
    {
        // 挪超 罗森博格 4:1 斯达，让 -2 → 让后 +1，让胜。
        // 台账押让负 @2.10（reverse）→ LOSE。这里最容易看走眼：主队大胜 3 球，但盘是 -2，
        // 「让负」并不因为主队赢球就成立。
        Assert.Equal('3', BdSpfSettlement.Result(4, 1, -2m));
        Assert.False(BdSpfSettlement.Judge('0', 4, 1, -2m));
    }

    [Fact]
    public void Iraklis_1v2_Plus2_IsHandicapWin()
    {
        // 希腊超 伊拉克利斯 1:2 帕纳辛纳科斯，受让 2 球 → 让后 +1，让胜。台账 @1.42 → WIN。
        // 这是「输球却赢盘」，结算时不能按胜负判。
        Assert.Equal('3', BdSpfSettlement.Result(1, 2, 2m));
        Assert.True(BdSpfSettlement.Judge('3', 1, 2, 2m));
    }

    // ---- 边界 ----

    [Fact]
    public void NetAfterHandicap_HandlesZeroAndNegative()
    {
        Assert.Equal(1, BdSpfSettlement.NetAfterHandicap(2, 1, 0m));
        Assert.Equal(0, BdSpfSettlement.NetAfterHandicap(2, 2, 0m));
        Assert.Equal(-3, BdSpfSettlement.NetAfterHandicap(0, 3, 0m));
        Assert.Equal(-2, BdSpfSettlement.NetAfterHandicap(0, 3, 1m));
    }

    [Fact]
    public void AllThreeSides_CanBeJudgedIndependently()
    {
        // 同一场 2:1 让 -1 只能有一个方向成立，三方向命中数恒为 1。
        var sides = new[] { '3', '1', '0' };
        foreach (var (home, away, hcp) in new[] { (2, 1, -1m), (3, 1, -1m), (0, 0, 0m) })
        {
            var wins = sides.Count(s => BdSpfSettlement.Judge(s, home, away, hcp));
            Assert.Equal(1, wins);
        }
    }
}
