using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

public class LottoHitMathTests
{
    [Fact]
    public void Ssq_Partial_Or_Full_Match_Is_PrizeUndetermined_Not_Lost_Or_Won()
    {
        var partial = LottoHitMath.Mark(
            "单式",
            [1, 5, 9, 12, 18, 33],
            [7],
            [5, 9, 12, 20, 21, 33],
            [16]);
        Assert.Equal(LottoHitStatus.PrizeUndetermined, partial.Status);
        Assert.Equal(4, partial.RedHits);
        Assert.True(partial.HasMatchDetail);
        Assert.False(LottoHitStatusRules.CountsInHitRate(partial.Status));
        Assert.False(LottoHitStatusRules.ShowAsWin(partial.Status));
        Assert.Contains("奖级未判定", partial.Note);

        var full = LottoHitMath.Mark(
            "单式",
            [1, 5, 9, 12, 18, 33],
            [16],
            [1, 5, 9, 12, 18, 33],
            [16]);
        Assert.Equal(LottoHitStatus.PrizeUndetermined, full.Status);
        Assert.False(LottoHitStatusRules.ShowAsWin(full.Status));
    }

    [Fact]
    public void Fushi_And_Dantuo_And_Kl8_Are_PrizeUndetermined()
    {
        var fushi = LottoHitMath.Mark(
            "复式",
            [1, 2, 3, 4, 5, 6, 7],
            [8],
            [1, 2, 3, 4, 5, 6],
            [8]);
        Assert.Equal(LottoHitStatus.PrizeUndetermined, fushi.Status);
        Assert.Equal(6, fushi.RedHits);

        var dan = LottoHitMath.Mark("四胆全拖", [1, 2, 3, 4], [5, 6], [1, 2, 3, 4, 9], [5, 6]);
        Assert.Equal(LottoHitStatus.PrizeUndetermined, dan.Status);

        var kl8 = LottoHitMath.Mark("选十", Enumerable.Range(1, 10).ToList(), [], Enumerable.Range(1, 20).ToArray(), []);
        Assert.Equal(LottoHitStatus.PrizeUndetermined, kl8.Status);
        Assert.Equal(10, kl8.RedHits);
    }

    [Fact]
    public void Digit_Direct_And_Group_Keep_Won_Lost()
    {
        var won = LottoHitMath.Mark("直选", [7, 3, 5], [], [7, 3, 5, 6, 9, 0], [], LottoCodes.Fc3d);
        Assert.Equal(LottoHitStatus.Won, won.Status);

        var lost = LottoHitMath.Mark("直选", [7, 3, 0], [], [7, 3, 5], [], LottoCodes.Fc3d);
        Assert.Equal(LottoHitStatus.Lost, lost.Status);

        var g6 = LottoHitMath.Mark("组选六", [1, 2, 3], [], [3, 1, 2], [], LottoCodes.Fc3d);
        Assert.Equal(LottoHitStatus.Won, g6.Status);
    }

    [Fact]
    public void GroupThree_AAB_Does_Not_Hit_ABB()
    {
        var g = LottoHitMath.Mark("组选三", [1, 1, 2], [], [1, 2, 2], [], LottoCodes.Fc3d);
        Assert.Equal(LottoHitStatus.Lost, g.Status);
        Assert.False(LottoHitStatusRules.ShowAsWin(g.Status));
    }

    [Fact]
    public void GroupThree_Two_Digit_AB_Rejected_Illegal()
    {
        var g = LottoHitMath.Mark("组选三", [1, 2], [], [1, 1, 2], [], LottoCodes.Fc3d);
        Assert.Equal(LottoHitStatus.Illegal, g.Status);
        Assert.False(LottoHitStatusRules.CountsInHitRate(g.Status));
    }

    [Fact]
    public void Note_With_Zhong_But_Not_Won_Must_Not_Show_As_Win()
    {
        var g = LottoHitMath.Mark("组选六", [1, 2, 4], [], [1, 2, 3], [], LottoCodes.Fc3d);
        Assert.Equal(LottoHitStatus.Lost, g.Status);
        Assert.Contains("未中", g.Note);
        Assert.Contains("中", g.Note);
        Assert.False(LottoHitStatusRules.ShowAsWin(g.Status));
    }

    [Fact]
    public void Unknown_Play_Undetermined()
    {
        var g = LottoHitMath.Mark("神秘玩法", [1, 2, 3], [], [1, 2, 3], [], LottoCodes.Fc3d);
        Assert.Equal(LottoHitStatus.Undetermined, g.Status);
        Assert.False(LottoHitStatusRules.CountsInHitRate(g.Status));
    }
}
