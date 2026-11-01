using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

public class LottoHitMathTests
{
    [Fact]
    public void Single_Marks_Reds_And_Blue_That_Came_Up()
    {
        var g = LottoHitMath.Mark(
            "单式",
            [1, 5, 9, 12, 18, 33],
            [7],
            [5, 9, 12, 20, 21, 33],
            [16]);

        Assert.Equal([false, true, true, true, false, true], g.RedHit);
        Assert.Equal([false], g.BlueHit);
        Assert.Equal(4, g.RedHits);
        Assert.Equal("红球中 4/6，蓝球中 0/1", g.Note);
        Assert.Equal([true, true, true, false, false, true], g.DrawRedHit);
    }

    [Fact]
    public void Direct_Marks_The_Same_Position()
    {
        var g = LottoHitMath.Mark("直选", [3, 5, 8], [], [3, 9, 8], []);

        Assert.Equal([true, false, true], g.RedHit);
        Assert.Equal([true, false, true], g.DrawRedHit);
        Assert.Equal("位置中 2/3", g.Note);
    }

    [Fact]
    public void Group_Ignores_Order_And_Does_Not_Reuse_A_Digit()
    {
        var all = LottoHitMath.Mark("组选六", [1, 2, 3], [], [3, 1, 2], []);
        Assert.Equal([true, true, true], all.RedHit);
        Assert.Equal("号码中 3/3", all.Note);

        var pair = LottoHitMath.Mark("组选三", [1, 1, 2], [], [1, 2, 2], []);
        Assert.Equal([true, false, true], pair.RedHit);
        Assert.Equal([true, true, false], pair.DrawRedHit);
        Assert.Equal("号码中 2/3", pair.Note);
    }

    [Fact]
    public void DanTuo_Marks_Dan_And_Back_Only()
    {
        var g = LottoHitMath.Mark(
            "四胆全拖",
            [1, 2, 3, 4],
            [5, 6],
            [2, 3, 10, 11, 12],
            [6, 11]);

        Assert.Equal([false, true, true, false], g.RedHit);
        Assert.Equal([false, true], g.BlueHit);
        Assert.Equal("胆中 2/4，后区中 1/2", g.Note);
        Assert.Equal([true, true, false, false, false], g.DrawRedHit);
    }

    [Fact]
    public void Digit_Games_Match_Place_And_Ignore_The_Extra_Numbers()
    {
        // 2026290 福彩3D 开奖 7 3 5，库里后面还有 6 9 0。组选也不能拿后面的号算中。
        var g = LottoHitMath.Mark("组选六", [7, 9, 0], [], [7, 3, 5, 6, 9, 0], [], LottoCodes.Fc3d);

        Assert.Equal([true, false, false], g.RedHit);
        Assert.Equal([true, false, false], g.DrawRedHit);
        Assert.Equal("百位中", g.Note);

        var pl5 = LottoHitMath.Mark("直选", [0, 3, 1, 3, 6], [], [0, 3, 9, 3, 6], [], LottoCodes.Pl5);
        Assert.Equal([true, true, false, true, true], pl5.RedHit);
        Assert.Equal("万位、千位、十位、个位中", pl5.Note);
    }

    [Fact]
    public void SelectTen_Counts_How_Many_Of_The_Ten_Were_Drawn()
    {
        var draw = Enumerable.Range(1, 20).ToArray();
        var g = LottoHitMath.Mark("选十", [1, 2, 3, 4, 30, 31, 32, 33, 34, 35], [], draw, []);

        Assert.Equal(4, g.RedHits);
        Assert.Equal("中 4/10", g.Note);
    }
}
